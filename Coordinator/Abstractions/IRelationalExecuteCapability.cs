using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// Capability to execute non-query SQL commands (INSERT / UPDATE / DELETE / DDL).
/// </summary>
public interface IRelationalExecuteCapability
{
    Task<RelationalExecuteResult> ExecuteAsync(RelationalExecuteRequest request, Transaction transaction, CancellationToken ct = default);
}
