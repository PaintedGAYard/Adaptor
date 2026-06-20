namespace Adaptor.Coordinator.Configuration;

public sealed class CoordinatorOptions
{
    public TimeSpan DefaultTransactionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Idle timeout before automatic session rollback.</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Server-enforced cap on user-supplied transaction timeouts.</summary>
    public TimeSpan MaxTransactionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan BlobStreamTransactionTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Server-enforced cap on Blob Stream transaction timeouts.</summary>
    public TimeSpan MaxBlobStreamTransactionTimeout { get; set; } = TimeSpan.FromHours(72);

    public TimeSpan SessionCleanupInterval { get; set; } = TimeSpan.FromSeconds(30);
}
