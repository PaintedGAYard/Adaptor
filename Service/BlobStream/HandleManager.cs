using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Adaptor.Service.BlobStream;

/// <summary>
/// Manages the mapping from handle IDs to <see cref="HandleEntry"/> instances.
/// Responsibilities: allocate unique handle IDs, maintain the mapping,
/// periodically clean up expired handles, and enforce per-connection handle limits.
/// </summary>
internal sealed class HandleManager : IDisposable
{
    private const int MaxHandlesPerConnection = 64;

    private static readonly TimeSpan HandleIdleTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    private long _nextHandle;
    private readonly ConcurrentDictionary<long, HandleEntry> _handles = new();
    private readonly ILogger<HandleManager> _logger;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public HandleManager(ILogger<HandleManager> logger)
    {
        _logger = logger;
        _nextHandle = (long)(Random.Shared.NextInt64(1_000_000, 10_000_000) << 32);
        _cleanupTimer = new Timer(
            CleanupExpiredHandles, null,
            CleanupInterval, CleanupInterval);
    }

    public int ActiveHandleCount => _handles.Count;

    /// <summary>
    /// Register a new handle and return its ID.
    /// </summary>
    /// <exception cref="InvalidOperationException">Per-connection limit (<c>64</c>) exceeded</exception>
    public long Register(string connectionId, int loFd, string key, string transactionId)
    {
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
            throw new InvalidOperationException($"Handle ID collision: {handleId}");
        }

        _logger.LogDebug("Handle {HandleId} registered: key={Key}, fd={LoFd}, tx={TxId}",
            handleId, key, loFd, transactionId);

        return handleId;
    }

    /// <summary>
    /// Look up a handle by ID and update its last activity time.
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
    /// Remove and dispose a handle entry.
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
    /// Remove all handles associated with a transaction (called on commit or rollback).
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
    /// Remove all handles for a connection (WebSocket disconnect cleanup).
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
    /// Periodically clean up handles that have been idle beyond the timeout.
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
