namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// Driver 事务能力的运行时枚举
/// </summary>
[Flags]
public enum TransactionCapabilities
{
    None = 0,
    /// <summary>支持 IEnlistmentNotification（标准两阶段提交）</summary>
    TwoPhaseCommit = 1 << 0,
    /// <summary>支持可提升单阶段提交 PSPE</summary>
    Promotable = 1 << 1,
    /// <summary>支持补偿事务（Commit 失败后可回滚）</summary>
    Compensating = 1 << 2,
}

/// <summary>
/// <see cref="IResourceManager"/> 的事务能力扩展方法
/// </summary>
public static class ResourceManagerExtensions
{
    /// <summary>运行时查询 Driver 的事务能力</summary>
    public static TransactionCapabilities GetTransactionCapabilities(this IResourceManager rm)
    {
        var caps = TransactionCapabilities.None;
        if (rm is ITransactionalResourceManager)
            caps |= TransactionCapabilities.TwoPhaseCommit;

        if (rm.GetType().GetInterfaces().Any(i =>
            i.IsGenericType &&
            i.GetGenericTypeDefinition().FullName?.Contains("IPromotableSinglePhaseNotification") == true))
        {
            caps |= TransactionCapabilities.Promotable;
        }

        return caps;
    }
}
