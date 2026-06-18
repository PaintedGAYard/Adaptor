namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// 存储资源类型
/// </summary>
public enum ResourceType
{
    Sql,
    Vector,
    Blob,
}

/// <summary>
/// 最基础标记：该 Driver 是一个资源管理器。
/// 所有 Driver 必须实现此接口。
/// </summary>
public interface IResourceManager
{
    string Name { get; }
    ResourceType ResourceType { get; }

    /// <summary>
    /// 资源管理器的唯一标识（遵循 ADO 惯例）。
    /// 用于在 .NET System.Transactions 中唯一标识此 RM。
    /// 设置/获取方式由具体实现决定。
    /// </summary>
    Guid ResourceManagerIdentifier { get; }
}
