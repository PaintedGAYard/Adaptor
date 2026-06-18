using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// 关系数据库查询能力（SELECT 等返回结果集的命令）
/// </summary>
public interface IRelationalQueryCapability
{
    Task<RelationalQueryResult> QueryAsync(RelationalQueryRequest request, Transaction transaction, CancellationToken ct = default);
}
