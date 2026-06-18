using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// SQL 执行能力（INSERT / UPDATE / DELETE / DDL 等不返回结果集的命令）
/// </summary>
public interface ISqlExecuteCapability
{
    Task<SqlExecuteResult> ExecuteAsync(SqlExecuteRequest request, Transaction transaction, CancellationToken ct = default);
}
