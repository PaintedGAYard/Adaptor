namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// 健康检查能力
/// </summary>
public interface IHealthCheckCapability
{
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}
