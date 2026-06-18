using System.Transactions;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// 声明该 Driver 支持事务性 Enlistment。
/// 只有实现此接口的 Driver 才会参与分布式事务协调。
/// </summary>
public interface ITransactionalResourceManager : IResourceManager
{
    /// <summary>
    /// 将 Driver 注册到指定的 .NET Transaction 中，
    /// Driver 内部需调用 <see cref="Transaction.EnlistVolatile"/> 或
    /// <see cref="Transaction.EnlistDurable"/> 完成注册。
    /// </summary>
    void Enlist(Transaction transaction);
}
