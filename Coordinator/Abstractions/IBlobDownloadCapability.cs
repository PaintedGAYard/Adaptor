using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// BLOB 下载能力
/// </summary>
public interface IBlobDownloadCapability
{
    Task<BlobDownloadResult> DownloadAsync(BlobDownloadRequest request, Transaction transaction, CancellationToken ct = default);
}
