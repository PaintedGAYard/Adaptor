using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

public interface IBlobUploadCapability
{
    /// <param name="request">The blob key, data, optional content-type, and metadata.</param>
    /// <param name="transaction">The distributed transaction to enlist in. Must be active.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="BlobUploadResult"/> with <c>ErrorMessage</c> null on success.</returns>
    Task<BlobUploadResult> UploadAsync(BlobUploadRequest request, Transaction transaction, CancellationToken ct = default);
}
