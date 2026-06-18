namespace Adaptor.Service.BlobStream;

/// <summary>
/// Handle 状态记录。
/// 每个 entry 对应客户端一个已打开的 BLOB 句柄，
/// 关联到底层 PostgreSQL Large Object 文件描述符。
/// </summary>
internal sealed class HandleEntry : IDisposable
{
    /// <summary>客户端可见的 handle ID</summary>
    public long HandleId { get; init; }

    /// <summary>PostgreSQL Large Object 文件描述符</summary>
    public int LoFd { get; init; }

    /// <summary>BLOB 键</summary>
    public string Key { get; init; } = "";

    /// <summary>关联的 .NET 事务 LocalIdentifier</summary>
    public string TransactionId { get; init; } = "";

    /// <summary>所属 gRPC 连接 ID</summary>
    public string ConnectionId { get; init; } = "";

    /// <summary>最后活动时间（用于超时 GC）</summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>Per-handle 串行化锁，保证 Seek+Read/Write 的原子性</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose()
    {
        Gate.Dispose();
    }
}
