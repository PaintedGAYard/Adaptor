using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// SQL 查询能力（SELECT 等返回结果集的命令）
/// </summary>
public interface ISqlQueryCapability
{
    Task<SqlQueryResult> QueryAsync(SqlQueryRequest request, Transaction transaction, CancellationToken ct = default);
}
