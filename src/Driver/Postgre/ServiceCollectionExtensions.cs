using Adaptor.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Adaptor.Driver.Postgre;

public static class ServiceCollectionExtensions
{
    #region String-based registration (backward-compatible)

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

    #endregion

    #region DataSource-based registration (recommended)

    /// <summary>
    /// Register all three PostgreSQL drivers using a shared <see cref="NpgsqlDataSource"/>.
    /// The DataSource should be configured with <c>UseVector()</c> for pgvector type mappings.
    /// </summary>
    /// <remarks>
    /// Using a DataSource is the recommended approach because:
    /// 1. It enables pgvector-dotnet type mappings via <c>dataSourceBuilder.UseVector()</c>
    /// 2. It provides connection pooling managed by Npgsql
    /// 3. It avoids repeated connection-string parsing
    /// 
    /// Note: All three drivers share the same DataSource, but each maintains its own
    /// connection manager and transaction tracking.
    /// </remarks>
    public static IServiceCollection AddAdaptorPostgreDrivers(
        this IServiceCollection services,
        NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        services.AddAdaptorDriver<PostgreSqlDriver>(sp =>
            new PostgreSqlDriver(dataSource));

        services.AddAdaptorDriver<PgVectorDriver>(sp =>
            new PgVectorDriver(dataSource));

        services.AddAdaptorDriver<PostgresBlobDriver>(sp =>
            new PostgresBlobDriver(dataSource));

        return services;
    }

    /// <summary>Register the PostgreSQL SQL driver with a DataSource.</summary>
    public static IServiceCollection AddAdaptorPostgreSqlDriver(
        this IServiceCollection services,
        NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        services.AddAdaptorDriver<PostgreSqlDriver>(sp =>
            new PostgreSqlDriver(dataSource));

        return services;
    }

    /// <summary>
    /// Register the pgvector driver with a DataSource.
    /// The DataSource should be configured with <c>UseVector()</c> to enable
    /// pgvector-dotnet CLR type mappings (<see cref="Pgvector.Vector"/>, <see cref="Pgvector.SparseVector"/>).
    /// </summary>
    public static IServiceCollection AddAdaptorPgVectorDriver(
        this IServiceCollection services,
        NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        services.AddAdaptorDriver<PgVectorDriver>(sp =>
            new PgVectorDriver(dataSource));

        return services;
    }

    /// <summary>Register the PostgreSQL BLOB driver with a DataSource.</summary>
    public static IServiceCollection AddAdaptorPostgresBlobDriver(
        this IServiceCollection services,
        NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        services.AddAdaptorDriver<PostgresBlobDriver>(sp =>
            new PostgresBlobDriver(dataSource));

        return services;
    }

    #endregion

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
