using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// 关系数据库向量搜索能力（相似度搜索）
/// </summary>
public interface IRelationalVectorSearchCapability
{
    Task<VectorSearchResult> SearchAsync(RelationalVectorSearchRequest request, Transaction transaction, CancellationToken ct = default);
}
