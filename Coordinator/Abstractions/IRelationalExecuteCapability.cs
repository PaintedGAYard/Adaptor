using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// 关系数据库执行能力（INSERT / UPDATE / DELETE / DDL 等不返回结果集的命令）
/// </summary>
public interface IRelationalExecuteCapability
{
    Task<RelationalExecuteResult> ExecuteAsync(RelationalExecuteRequest request, Transaction transaction, CancellationToken ct = default);
}
