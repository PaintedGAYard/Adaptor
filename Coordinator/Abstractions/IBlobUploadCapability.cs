using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// BLOB 上传能力
/// </summary>
public interface IBlobUploadCapability
{
    Task<BlobUploadResult> UploadAsync(BlobUploadRequest request, Transaction transaction, CancellationToken ct = default);
}
