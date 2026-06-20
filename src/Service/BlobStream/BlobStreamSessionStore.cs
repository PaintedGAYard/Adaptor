using System.Collections.Concurrent;

namespace Adaptor.Service.BlobStream;

/// <summary>
/// BlobStream session parameters negotiated by gRPC <c>BeginSession</c>.
/// </summary>
public sealed record BlobStreamSessionParams(
    TimeSpan NegotiatedTimeout,
    int NegotiatedChunkSize,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// Internal session entry tracking a BlobStream session.
/// </summary>
public sealed class BlobStreamSessionEntry : IDisposable
{
    public string Token { get; init; } = "";
    public BlobStreamSessionParams Parameters { get; init; } = null!;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; }

    /// <summary>Set after WebSocket connects; signals gRPC EndSession to close WS.</summary>
    public CancellationTokenSource? WsCancellation { get; set; }

    /// <summary>Associated transaction local identifier; set when a WS connects.</summary>
    public string? TransactionId { get; set; }

    /// <summary>Active WebSocket connection ID; null when disconnected (paused).</summary>
    public string? ConnectionId { get; set; }

    /// <summary>Whether a WebSocket connection is currently established.</summary>
    public bool IsConnected => ConnectionId != null;

    /// <summary>
    /// Timestamp when the WebSocket disconnected and the session entered pause state.
    /// Null when not paused.
    /// </summary>
    public DateTime? PauseStartedAt { get; set; }

    /// <summary>Number of times the session has reconnected after a pause.</summary>
    public int ReconnectCount { get; set; }

    public void Dispose()
    {
        WsCancellation?.Cancel();
        WsCancellation?.Dispose();
    }
}

/// <summary>
/// BlobStream session store managing session_token to session parameter mappings.
/// Supports: gRPC BeginSession (create), WebSocket connect (validate/reconnect),
/// gRPC EndSession (invalidate + close WS), and background GC for expired/paused sessions.
/// </summary>
public sealed class BlobStreamSessionStore : IDisposable
{
    private static readonly TimeSpan MaxSessionAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultPausedTransactionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, BlobStreamSessionEntry> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _pausedTransactionTimeout;
    private bool _disposed;

    public BlobStreamSessionStore() : this(DefaultPausedTransactionTimeout) { }

    public BlobStreamSessionStore(TimeSpan pausedTransactionTimeout)
    {
        _pausedTransactionTimeout = pausedTransactionTimeout;
        _cleanupTimer = new Timer(CleanupExpiredSessions, null,
            CleanupInterval, CleanupInterval);
    }

    /// <summary>
    /// Create a new session and return its entry.
    /// </summary>
    public BlobStreamSessionEntry Create(TimeSpan negotiatedTimeout, int negotiatedChunkSize,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var token = Guid.NewGuid().ToString("N");
        var entry = new BlobStreamSessionEntry
        {
            Token = token,
            Parameters = new BlobStreamSessionParams(negotiatedTimeout, negotiatedChunkSize, metadata),
            ExpiresAt = DateTime.UtcNow + MaxSessionAge,
        };

        _sessions[token] = entry;
        return entry;
    }

    /// <summary>
    /// Validate and retrieve session parameters (called on WebSocket connect).
    /// Returns null if the token does not exist or has expired.
    /// </summary>
    public BlobStreamSessionEntry? Validate(string token)
    {
        if (_sessions.TryGetValue(token, out var entry))
        {
            if (DateTime.UtcNow < entry.ExpiresAt)
            {
                return entry;
            }

            _sessions.TryRemove(token, out _);
            entry.Dispose();
        }
        return null;
    }

    /// <summary>
    /// Attempt to reconnect to an existing session that was paused (WS disconnected).
    /// Validates that the session exists, is not expired, and has not exceeded the pause timeout.
    /// On success, updates the connection state and increments the reconnect count.
    /// </summary>
    /// <param name="token">The session token.</param>
    /// <param name="newConnectionId">The new WebSocket connection ID.</param>
    /// <returns>The session entry if reconnect is allowed; null otherwise.</returns>
    public BlobStreamSessionEntry? TryReconnect(string token, string newConnectionId)
    {
        if (!_sessions.TryGetValue(token, out var entry))
            return null;

        if (DateTime.UtcNow >= entry.ExpiresAt)
        {
            _sessions.TryRemove(token, out _);
            entry.Dispose();
            return null;
        }

        // If the session is paused, check if the pause timeout has expired.
        if (entry.PauseStartedAt.HasValue)
        {
            var pauseDuration = DateTime.UtcNow - entry.PauseStartedAt.Value;
            if (pauseDuration >= _pausedTransactionTimeout)
            {
                // Pause timeout exceeded — session cannot be reconnected.
                _sessions.TryRemove(token, out _);
                entry.Dispose();
                return null;
            }
        }

        // Update connection state.
        entry.ConnectionId = newConnectionId;
        entry.PauseStartedAt = null;
        entry.ReconnectCount++;
        return entry;
    }

    /// <summary>
    /// Get session by token without expiry check.
    /// </summary>
    public BlobStreamSessionEntry? Get(string token)
    {
        _sessions.TryGetValue(token, out var entry);
        return entry;
    }

    /// <summary>
    /// Invalidate a token and close its associated WebSocket connection.
    /// Called by gRPC EndSession.
    /// </summary>
    public bool Invalidate(string token)
    {
        if (_sessions.TryRemove(token, out var entry))
        {
            entry.Dispose(); // Triggers WsCancellation.Cancel()
            return true;
        }
        return false;
    }

    /// <summary>
    /// Mark a session as paused (WS disconnected). Sets <see cref="BlobStreamSessionEntry.PauseStartedAt"/>
    /// and clears <see cref="BlobStreamSessionEntry.ConnectionId"/>.
    /// </summary>
    public void MarkPaused(string token)
    {
        if (_sessions.TryGetValue(token, out var entry))
        {
            entry.ConnectionId = null;
            entry.PauseStartedAt ??= DateTime.UtcNow; // Only set on first pause.
        }
    }

    private void CleanupExpiredSessions(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        foreach (var (token, entry) in _sessions)
        {
            if (now >= entry.ExpiresAt)
            {
                if (_sessions.TryRemove(token, out var removed))
                {
                    removed.Dispose();
                }
                continue;
            }

            // Clean up sessions that have been paused beyond the timeout.
            if (entry.PauseStartedAt.HasValue)
            {
                var pauseDuration = now - entry.PauseStartedAt.Value;
                if (pauseDuration >= _pausedTransactionTimeout)
                {
                    if (_sessions.TryRemove(token, out var removed))
                    {
                        removed.Dispose();
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();
        foreach (var (_, entry) in _sessions)
        {
            entry.Dispose();
        }
        _sessions.Clear();
    }
}
