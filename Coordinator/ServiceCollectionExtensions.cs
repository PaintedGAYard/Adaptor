using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Configuration;
using Adaptor.Coordinator.Plugins;
using Adaptor.Coordinator.Services;

namespace Adaptor.Coordinator;

/// <summary>
/// 用于将 Coordinator 服务注册到 DI 容器的扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Adaptor 协调器核心服务 + SK Plugin
    /// </summary>
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

        // 注册 SK Plugin
        services.TryAddSingleton<TransactionPlugin>();
        services.TryAddSingleton<RelationalPlugin>();
        services.TryAddSingleton<VectorSearchPlugin>();

        return services;
    }

    /// <summary>
    /// 将已注册的 Adaptor Plugin 加载到 SK Kernel 中
    /// </summary>
    public static IServiceCollection AddAdaptorPluginsToKernel(
        this IServiceCollection services)
    {
        // 使用 SK KernelBuilder 的标准插件注册方式
        // Consumer 需要在构建 Kernel 后通过 kernel.Plugins.AddFromObject 加载
        // 或使用 AddKernel 后的 PostConfigure 模式
        return services;
    }

    /// <summary>
    /// 注册一个 Driver 到 DI 容器（无参构造）
    /// </summary>
    public static IServiceCollection AddAdaptorDriver<TDriver>(
        this IServiceCollection services)
        where TDriver : class, IResourceManager
    {
        services.AddSingleton<IResourceManager, TDriver>();
        return services;
    }

    /// <summary>
    /// 注册一个 Driver 到 DI 容器（工厂模式）
    /// </summary>
    public static IServiceCollection AddAdaptorDriver<TDriver>(
        this IServiceCollection services,
        Func<IServiceProvider, TDriver> factory)
        where TDriver : class, IResourceManager
    {
        services.AddSingleton<IResourceManager>(sp => factory(sp));
        return services;
    }
}
