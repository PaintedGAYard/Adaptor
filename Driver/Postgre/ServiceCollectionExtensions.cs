using Adaptor.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Adaptor.Driver.Postgre;

/// <summary>
/// 用于将 PostgreSQL Driver 注册到 DI 容器的扩展方法。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册全部三个 PostgreSQL Driver（SQL + pgvector + BLOB LO），
    /// 共享同一个连接字符串。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="connectionString">PostgreSQL 连接字符串</param>
    /// <returns>服务集合（支持链式调用）</returns>
    public static IServiceCollection AddAdaptorPostgreDrivers(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddAdaptorDriver<PostgreSqlDriver>(sp =>
            new PostgreSqlDriver(connectionString));

        services.AddAdaptorDriver<PgVectorDriver>(sp =>
            new PgVectorDriver(connectionString));

        services.AddAdaptorDriver<PostgresBlobDriver>(sp =>
            new PostgresBlobDriver(connectionString));

        return services;
    }

    /// <summary>
    /// 注册 PostgreSQL SQL Driver。
    /// </summary>
    public static IServiceCollection AddAdaptorPostgreSqlDriver(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddAdaptorDriver<PostgreSqlDriver>(sp =>
            new PostgreSqlDriver(connectionString));

        return services;
    }

    /// <summary>
    /// 注册 pgvector Vector Driver。
    /// </summary>
    public static IServiceCollection AddAdaptorPgVectorDriver(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddAdaptorDriver<PgVectorDriver>(sp =>
            new PgVectorDriver(connectionString));

        return services;
    }

    /// <summary>
    /// 注册 PostgreSQL BLOB (Large Object) Driver。
    /// </summary>
    public static IServiceCollection AddAdaptorPostgresBlobDriver(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddAdaptorDriver<PostgresBlobDriver>(sp =>
            new PostgresBlobDriver(connectionString));

        return services;
    }

    // ─── Internal helper (same pattern as Coordinator's AddAdaptorDriver) ───

    private static void AddAdaptorDriver<TDriver>(
        this IServiceCollection services,
        Func<IServiceProvider, TDriver> factory)
        where TDriver : class, Coordinator.Abstractions.IResourceManager
    {
        services.AddSingleton<Coordinator.Abstractions.IResourceManager>(factory);
    }
}
