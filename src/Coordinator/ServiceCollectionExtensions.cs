using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Configuration;
using Adaptor.Coordinator.Plugins;
using Adaptor.Coordinator.Services;

namespace Adaptor.Coordinator;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register coordinator core services and SK plugins.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="TransactionPlugin"/>, <see cref="RelationalPlugin"/>, and <see cref="VectorSearchPlugin"/>.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional; pass <c>null</c> to use defaults.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAdaptorCoordinator(
        this IServiceCollection services,
        Action<CoordinatorOptions>? configureOptions = null)
    {
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<TransactionCoordinator>();

        services.TryAddSingleton<TransactionPlugin>();
        services.TryAddSingleton<RelationalPlugin>();
        services.TryAddSingleton<VectorSearchPlugin>();

        return services;
    }

    public static IServiceCollection AddAdaptorPluginsToKernel(
        this IServiceCollection services)
    {
        return services;
    }

    /// <summary>
    /// Register a driver (parameterless constructor).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <typeparam name="TDriver">Driver type implementing <see cref="IResourceManager"/>.</typeparam>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAdaptorDriver<TDriver>(
        this IServiceCollection services)
        where TDriver : class, IResourceManager
    {
        services.AddSingleton<IResourceManager, TDriver>();
        return services;
    }

    /// <param name="factory">Factory delegate for creating the driver instance.</param>
    public static IServiceCollection AddAdaptorDriver<TDriver>(
        this IServiceCollection services,
        Func<IServiceProvider, TDriver> factory)
        where TDriver : class, IResourceManager
    {
        services.AddSingleton<IResourceManager>(sp => factory(sp));
        return services;
    }
}
