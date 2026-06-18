using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Configuration;
using Adaptor.Coordinator.Services;

namespace Adaptor.Coordinator;

/// <summary>
/// 用于将 Coordinator 服务注册到 DI 容器的扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Adaptor 协调器核心服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configureOptions">配置选项委托</param>
    /// <returns>服务集合（支持链式调用）</returns>
    public static IServiceCollection AddAdaptorCoordinator(
        this IServiceCollection services,
        Action<CoordinatorOptions>? configureOptions = null)
    {
        // 配置选项
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        // 注册会话管理器（单例）和事务协调器（单例）
        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<TransactionCoordinator>();

        return services;
    }

    /// <summary>
    /// 注册一个 Driver 到 DI 容器（无参构造）
    /// </summary>
    /// <typeparam name="TDriver">Driver 类型（必须实现 <see cref="IResourceManager"/>）</typeparam>
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
    /// <typeparam name="TDriver">Driver 类型（必须实现 <see cref="IResourceManager"/>）</typeparam>
    /// <param name="factory">创建 Driver 实例的工厂委托</param>
    public static IServiceCollection AddAdaptorDriver<TDriver>(
        this IServiceCollection services,
        Func<IServiceProvider, TDriver> factory)
        where TDriver : class, IResourceManager
    {
        services.AddSingleton<IResourceManager>(sp => factory(sp));
        return services;
    }
}
