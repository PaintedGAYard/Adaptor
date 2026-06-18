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

    public string? ConnectionId { get; set; }

    /// <summary>Whether a WebSocket connection has been established.</summary>
    public bool IsConnected => ConnectionId != null;

    public void Dispose()
    {
        WsCancellation?.Cancel();
        WsCancellation?.Dispose();
    }
}

/// <summary>
/// BlobStream session store managing session_token to session parameter mappings.
/// Supports: gRPC BeginSession (create), WebSocket connect (validate),
/// gRPC EndSession (invalidate + close WS), and background GC for expired sessions.
/// </summary>
public sealed class BlobStreamSessionStore : IDisposable
{
    private static readonly TimeSpan MaxSessionAge = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, BlobStreamSessionEntry> _sessions = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public BlobStreamSessionStore()
    {
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
