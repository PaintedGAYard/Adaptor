using System.Collections.Concurrent;
using System.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Adaptor.Coordinator.Configuration;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Services;

/// <summary>
/// 会话管理器：管理 Consumer 与会话的映射，处理空闲超时和自动回滚。
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _connectionSessions = new(); // connectionId -> sessionIds
    private readonly CoordinatorOptions _options;
    private readonly ILogger<SessionManager> _logger;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// 当会话因超时而自动回滚时触发
    /// </summary>
    public event Action<SessionContext>? OnSessionTimeout;

    public SessionManager(
        IOptions<CoordinatorOptions> options,
        ILogger<SessionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
        _cleanupTimer = new Timer(
            CleanupExpiredSessions,
            null,
            _options.SessionCleanupInterval,
            _options.SessionCleanupInterval);
    }

    /// <summary>
    /// 创建新会话
    /// </summary>
    /// <param name="transaction">关联的 .NET Transaction 对象</param>
    public SessionContext CreateSession(Transaction transaction, string? connectionId = null)
    {
        var session = new SessionContext
        {
            SessionId = Guid.NewGuid().ToString("N"),
            TransactionLocalIdentifier = transaction.TransactionInformation.LocalIdentifier,
        };

        _sessions[session.SessionId] = session;

        if (connectionId != null)
        {
            _connectionSessions.AddOrUpdate(
                connectionId,
                _ => new HashSet<string> { session.SessionId },
                (_, set) => { set.Add(session.SessionId); return set; });
        }

        _logger.LogDebug("Session {SessionId} created for transaction '{LocalId}'",
            session.SessionId, transaction.TransactionInformation.LocalIdentifier);

        return session;
    }

    /// <summary>
    /// 获取会话
    /// </summary>
    public SessionContext? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    /// 将会话关联到 gRPC 连接
    /// </summary>
    public void AttachToConnection(string sessionId, string connectionId)
    {
        if (!_sessions.ContainsKey(sessionId)) return;

        _connectionSessions.AddOrUpdate(
            connectionId,
            _ => new HashSet<string> { sessionId },
            (_, set) => { set.Add(sessionId); return set; });
    }

    /// <summary>
    /// 移除并关闭会话
    /// </summary>
    public bool RemoveSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.IsClosed = true;
            _logger.LogDebug("Session {SessionId} removed", sessionId);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 当 gRPC 连接断开时，回滚关联的所有活跃会话
    /// </summary>
    public IReadOnlyList<SessionContext> OnConnectionClosed(string connectionId)
    {
        var affected = new List<SessionContext>();

        if (_connectionSessions.TryRemove(connectionId, out var sessionIds))
        {
            foreach (var sessionId in sessionIds)
            {
                if (_sessions.TryRemove(sessionId, out var session))
                {
                    session.IsClosed = true;
                    affected.Add(session);
                    _logger.LogWarning(
                        "Connection {ConnectionId} closed, session {SessionId} (tx='{LocalId}') will be rolled back",
                        connectionId, sessionId, session.TransactionLocalIdentifier);
                }
            }
        }

        return affected;
    }

    /// <summary>
    /// 更新会话的活动时间
    /// </summary>
    public void TouchSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Touch();
        }
    }

    private void CleanupExpiredSessions(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var expiredIds = new List<string>();

        foreach (var (sessionId, session) in _sessions)
        {
            if (session.IsClosed)
            {
                expiredIds.Add(sessionId);
                continue;
            }

            var idleDuration = now - session.LastActivityAt;
            if (idleDuration >= _options.SessionIdleTimeout)
            {
                expiredIds.Add(sessionId);
            }
        }

        foreach (var sessionId in expiredIds)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.IsClosed = true;
                _logger.LogWarning(
                    "Session {SessionId} (tx='{LocalId}') timed out after idle {IdleSeconds}s",
                    sessionId, session.TransactionLocalIdentifier, _options.SessionIdleTimeout.TotalSeconds);

                try
                {
                    OnSessionTimeout?.Invoke(session);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling timeout for session {SessionId}", sessionId);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();
        _sessions.Clear();
        _connectionSessions.Clear();
    }
}
