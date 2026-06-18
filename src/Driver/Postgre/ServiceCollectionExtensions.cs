using Adaptor.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Adaptor.Driver.Postgre;

public static class ServiceCollectionExtensions
{
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

    public static IServiceCollection AddAdaptorPostgreSqlDriver(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddAdaptorDriver<PostgreSqlDriver>(sp =>
            new PostgreSqlDriver(connectionString));

        return services;
    }

    public static IServiceCollection AddAdaptorPgVectorDriver(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddAdaptorDriver<PgVectorDriver>(sp =>
            new PgVectorDriver(connectionString));

        return services;
    }

    /// <summary>Register the PostgreSQL BLOB (Large Object) driver.</summary>
    public static IServiceCollection AddAdaptorPostgresBlobDriver(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddAdaptorDriver<PostgresBlobDriver>(sp =>
            new PostgresBlobDriver(connectionString));

        return services;
    }

    #region Internal helper

    private static void AddAdaptorDriver<TDriver>(
        this IServiceCollection services,
        Func<IServiceProvider, TDriver> factory)
        where TDriver : class, Coordinator.Abstractions.IResourceManager
    {
        services.AddSingleton<Coordinator.Abstractions.IResourceManager>(factory);
    }

    #endregion
}
