namespace Adaptor.Coordinator.Configuration;

/// <summary>
/// 协调器配置选项
/// </summary>
public sealed class CoordinatorOptions
{
    /// <summary>默认事务超时时间（默认 30 秒）</summary>
    public TimeSpan DefaultTransactionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>会话空闲超时（默认 60 秒）</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>每个 Driver Commit 阶段的最大重试次数（默认 3）</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>指数退避基数（默认 100ms）</summary>
    public TimeSpan RetryBackoffBase { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>服务端限制的最大超时（默认 5 分钟），防止 Consumer 设置过大超时</summary>
    public TimeSpan MaxTransactionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Blob Stream 事务超时（默认 24 小时），由 BeginBlobStreamTransaction 使用</summary>
    public TimeSpan BlobStreamTransactionTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Blob Stream 事务最大超时（默认 72 小时）</summary>
    public TimeSpan MaxBlobStreamTransactionTimeout { get; set; } = TimeSpan.FromHours(72);

    /// <summary>会话清理定时器间隔（默认 30 秒）</summary>
    public TimeSpan SessionCleanupInterval { get; set; } = TimeSpan.FromSeconds(30);
}
