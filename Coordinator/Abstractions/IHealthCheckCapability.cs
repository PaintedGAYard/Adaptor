namespace Adaptor.Coordinator.Abstractions;

public interface IHealthCheckCapability
{
    /// <returns><c>true</c> if the driver is healthy; <c>false</c> if unreachable or degraded.</returns>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}
