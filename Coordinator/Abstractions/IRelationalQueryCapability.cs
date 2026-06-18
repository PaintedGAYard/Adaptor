using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// Capability to execute SQL queries that return result sets (SELECT).
/// </summary>
public interface IRelationalQueryCapability
{
    Task<RelationalQueryResult> QueryAsync(RelationalQueryRequest request, Transaction transaction, CancellationToken ct = default);
}
