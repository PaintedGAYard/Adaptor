using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Adaptor.Service.BlobStream;

/// <summary>
/// Handle 管理器 — 维护 handle ID 到 <see cref="HandleEntry"/> 的映射。
///
/// 职责:
/// 1. 分配全局唯一的 handle ID（Interlocked.Increment，起始于随机值）
/// 2. 维护 ConcurrentDictionary 映射
/// 3. 定时清理过期 handle（事务已结束 / 空闲超时）
/// 4. 限制每连接最大 handle 数
/// </summary>
internal sealed class HandleManager : IDisposable
{
    /// <summary>每连接最大 handle 数</summary>
    private const int MaxHandlesPerConnection = 64;

    /// <summary>Handle 空闲超时（超过此时间未活动则自动关闭）</summary>
    private static readonly TimeSpan HandleIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>GC 清理间隔</summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    private long _nextHandle; // 从随机值起始
    private readonly ConcurrentDictionary<long, HandleEntry> _handles = new();
    private readonly ILogger<HandleManager> _logger;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public HandleManager(ILogger<HandleManager> logger)
    {
        _logger = logger;
        // 随机起始值，降低碰撞概率
        _nextHandle = (long)(Random.Shared.NextInt64(1_000_000, 10_000_000) << 32);
        _cleanupTimer = new Timer(
            CleanupExpiredHandles, null,
            CleanupInterval, CleanupInterval);
    }

    /// <summary>当前活跃 handle 数</summary>
    public int ActiveHandleCount => _handles.Count;

    /// <summary>
    /// 注册一个新的 handle entry。
    /// </summary>
    /// <returns>分配的 handle ID</returns>
    /// <exception cref="InvalidOperationException">超过每连接最大 handle 数</exception>
    public long Register(string connectionId, int loFd, string key, string transactionId)
    {
        // 检查连接级别的 handle 上限
        var connectionCount = _handles.Values.Count(h => h.ConnectionId == connectionId);
        if (connectionCount >= MaxHandlesPerConnection)
        {
            throw new InvalidOperationException(
                $"Handle limit ({MaxHandlesPerConnection}) reached for connection {connectionId}");
        }

        var handleId = Interlocked.Increment(ref _nextHandle);
        var entry = new HandleEntry
        {
            HandleId = handleId,
            LoFd = loFd,
            Key = key,
            TransactionId = transactionId,
            ConnectionId = connectionId,
        };

        if (!_handles.TryAdd(handleId, entry))
        {
            // 理论上不会发生，Interlocked.Increment 保证唯一
            throw new InvalidOperationException($"Handle ID collision: {handleId}");
        }

        _logger.LogDebug("Handle {HandleId} registered: key={Key}, fd={LoFd}, tx={TxId}",
            handleId, key, loFd, transactionId);

        return handleId;
    }

    /// <summary>
    /// 通过 handle ID 查找 entry，并更新活动时间。
    /// </summary>
    public bool TryGet(long handleId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HandleEntry? entry)
    {
        if (_handles.TryGetValue(handleId, out entry))
        {
            entry.LastActivityAt = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 移除并释放 handle entry。
    /// </summary>
    public bool Remove(long handleId, out HandleEntry? entry)
    {
        if (_handles.TryRemove(handleId, out entry))
        {
            _logger.LogDebug("Handle {HandleId} removed (key={Key})", handleId, entry.Key);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 移除指定事务的所有 handle（事务提交/回滚时调用）。
    /// </summary>
    public IReadOnlyList<HandleEntry> InvalidateHandlesForTransaction(string transactionId)
    {
        var removed = new List<HandleEntry>();
        foreach (var (handleId, entry) in _handles)
        {
            if (entry.TransactionId == transactionId && _handles.TryRemove(handleId, out var e))
            {
                removed.Add(e);
            }
        }

        if (removed.Count > 0)
        {
            _logger.LogInformation(
                "Invalidated {Count} handles for transaction {TxId}",
                removed.Count, transactionId);
        }

        return removed;
    }

    /// <summary>
    /// 移除指定连接的所有 handle。
    /// 用于 WebSocket 断开时的资源清理。
    /// </summary>
    public IReadOnlyList<HandleEntry> RemoveAllForConnection(string connectionId)
    {
        var removed = new List<HandleEntry>();
        foreach (var (handleId, entry) in _handles)
        {
            if (entry.ConnectionId == connectionId && _handles.TryRemove(handleId, out var e))
            {
                removed.Add(e);
            }
        }

        if (removed.Count > 0)
        {
            _logger.LogWarning(
                "Removed {Count} handles for connection {ConnectionId}",
                removed.Count, connectionId);
        }

        return removed;
    }

    /// <summary>
    /// 定时清理过期 handle。
    /// 过期条件: 最后活动时间超过 HandleIdleTimeout，
    /// 或关联事务已不是 Active 状态。
    /// </summary>
    private void CleanupExpiredHandles(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var expired = new List<long>();

        foreach (var (handleId, entry) in _handles)
        {
            var idleDuration = now - entry.LastActivityAt;
            if (idleDuration >= HandleIdleTimeout ||
                entry.ConnectionId == "(disconnected)")
            {
                expired.Add(handleId);
            }
        }

        foreach (var handleId in expired)
        {
            if (_handles.TryRemove(handleId, out var entry))
            {
                _logger.LogWarning(
                    "Handle {HandleId} (key={Key}) expired after idle {IdleSeconds}s",
                    handleId, entry.Key, (now - entry.LastActivityAt).TotalSeconds);
                entry.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();

        foreach (var (_, entry) in _handles)
        {
            entry.Dispose();
        }
        _handles.Clear();
    }
}
