using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

public interface IRelationalVectorSearchCapability
{
    /// <param name="request">Search parameters including table, vector column, query vector, top-K, and optional WHERE filter.</param>
    /// <param name="transaction">The distributed transaction to enlist in. Must be active.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="VectorSearchResult"/> with hits ordered by descending score.</returns>
    Task<VectorSearchResult> SearchAsync(RelationalVectorSearchRequest request, Transaction transaction, CancellationToken ct = default);
}
