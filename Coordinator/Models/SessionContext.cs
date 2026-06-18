namespace Adaptor.Coordinator.Models;

/// <summary>
/// 会话上下文，绑定到一个活跃事务
/// </summary>
public sealed class SessionContext
{
    /// <summary>会话唯一标识</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    /// 关联事务的 <see cref="System.Transactions.TransactionInformation.LocalIdentifier"/>。
    /// Coordinator 通过此标识查找 <see cref="System.Transactions.Transaction"/> 实例。
    /// </summary>
    public string TransactionLocalIdentifier { get; init; } = string.Empty;

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>最后活动时间</summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>是否已关闭</summary>
    public bool IsClosed { get; set; }

    /// <summary>更新最后活动时间</summary>
    public void Touch() => LastActivityAt = DateTime.UtcNow;
}
