using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

public interface IBlobDownloadCapability
{
    /// <param name="request">The blob key to download.</param>
    /// <param name="transaction">The distributed transaction to enlist in. Must be active.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="BlobDownloadResult"/> with <c>ErrorMessage</c> null on success.</returns>
    Task<BlobDownloadResult> DownloadAsync(BlobDownloadRequest request, Transaction transaction, CancellationToken ct = default);
}
