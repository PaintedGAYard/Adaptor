using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// 向量搜索能力（相似度搜索）
/// </summary>
public interface IVectorSearchCapability
{
    Task<VectorSearchResult> SearchAsync(VectorSearchRequest request, Transaction transaction, CancellationToken ct = default);
}
