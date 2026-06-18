using System.Collections.Concurrent;

namespace Adaptor.Service.BlobStream;

/// <summary>
/// BlobStream 会话参数，由 gRPC <c>BeginSession</c> 协商确定。
/// </summary>
public sealed record BlobStreamSessionParams(
    TimeSpan NegotiatedTimeout,
    int NegotiatedChunkSize,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// 会话条目（内部状态）。
/// </summary>
public sealed class BlobStreamSessionEntry : IDisposable
{
    public string Token { get; init; } = "";
    public BlobStreamSessionParams Parameters { get; init; } = null!;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; }

    /// <summary>WebSocket 连接后设置，用于主动关闭 WS</summary>
    public CancellationTokenSource? WsCancellation { get; set; }

    /// <summary>WebSocket 连接后设置，记录关联的连接 ID</summary>
    public string? ConnectionId { get; set; }

    /// <summary>是否已通过 WebSocket 建立连接</summary>
    public bool IsConnected => ConnectionId != null;

    public void Dispose()
    {
        WsCancellation?.Cancel();
        WsCancellation?.Dispose();
    }
}

/// <summary>
/// BlobStream 会话存储器。
///
/// 管理 session_token → 会话参数的映射，支持:
/// 1. gRPC BeginSession 创建会话
/// 2. WebSocket 连接时凭 token 获取参数
/// 3. gRPC EndSession 失效 token 并关闭关联 WS
/// 4. 后台 GC 清理过期会话
/// </summary>
public sealed class BlobStreamSessionStore : IDisposable
{
    /// <summary>会话最大空闲时间（BeginSession 后未连 WS）</summary>
    private static readonly TimeSpan MaxSessionAge = TimeSpan.FromMinutes(5);

    /// <summary>GC 清理间隔</summary>
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
    /// 创建新会话。
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
    /// 验证并获取会话参数（WebSocket 连接时调用）。
    /// 如果 token 不存在或已过期，返回 null。
    /// </summary>
    public BlobStreamSessionEntry? Validate(string token)
    {
        if (_sessions.TryGetValue(token, out var entry))
        {
            if (DateTime.UtcNow < entry.ExpiresAt)
            {
                return entry;
            }

            // 已过期，清理
            _sessions.TryRemove(token, out _);
            entry.Dispose();
        }
        return null;
    }

    /// <summary>
    /// 获取会话（不验证过期）。
    /// </summary>
    public BlobStreamSessionEntry? Get(string token)
    {
        _sessions.TryGetValue(token, out var entry);
        return entry;
    }

    /// <summary>
    /// 失效 token 并关闭关联的 WebSocket 连接。
    /// 由 gRPC EndSession 调用。
    /// </summary>
    public bool Invalidate(string token)
    {
        if (_sessions.TryRemove(token, out var entry))
        {
            entry.Dispose(); // 触发 WsCancellation.Cancel()
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
