using System.Collections.Concurrent;
using System.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Adaptor.Coordinator.Configuration;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Services;

/// <summary>
/// Manages session-to-consumer mappings, idle timeout, and automatic rollback.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _connectionSessions = new();
    private readonly CoordinatorOptions _options;
    private readonly ILogger<SessionManager> _logger;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Raised when a session is automatically rolled back due to timeout.
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
    /// Create a new session bound to the specified transaction.
    /// </summary>
    /// <param name="transaction">The .NET Transaction to associate with this session.</param>
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

    public SessionContext? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    /// Associate a session with a gRPC connection.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="connectionId">The gRPC connection identifier.</param>
    public void AttachToConnection(string sessionId, string connectionId)
    {
        if (!_sessions.ContainsKey(sessionId)) return;

        _connectionSessions.AddOrUpdate(
            connectionId,
            _ => new HashSet<string> { sessionId },
            (_, set) => { set.Add(sessionId); return set; });
    }

    /// <summary>
    /// Remove and close a session.
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
    /// When a gRPC connection is closed, roll back all associated active sessions.
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
    /// Update a session's last activity timestamp.
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
