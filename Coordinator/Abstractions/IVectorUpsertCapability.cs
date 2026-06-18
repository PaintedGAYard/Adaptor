using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// 向量写入能力（插入或更新向量）
/// </summary>
public interface IVectorUpsertCapability
{
    Task<VectorUpsertResult> UpsertAsync(VectorUpsertRequest request, Transaction transaction, CancellationToken ct = default);
}
