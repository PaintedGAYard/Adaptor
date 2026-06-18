using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

public enum BlobAccessMode
{
    Read = 1,
    /// <summary>Open existing blob for writing (from offset 0)</summary>
    Write = 2,
    ReadWrite = 3,
    /// <summary>Create a new blob; error if key already exists</summary>
    Create = 4,
    CreateOrReplace = 5,
    /// <summary>Open existing blob and append at the end</summary>
    Append = 6,
}

/// <summary>
/// Random-access BLOB operations with file-like Seek/Read/Write/Close semantics,
/// orthogonal to the upload/download capabilities.
/// </summary>
/// <remarks>The file descriptor (<c>loFd</c>) is obtained from <see cref="OpenAsync"/> and used in subsequent operations.</remarks>
public interface IBlobRandomAccessCapability
{
    /// <param name="key">The blob key.</param>
    /// <param name="mode">Access mode; <see cref="BlobAccessMode.Create"/> errors if the key exists.</param>
    /// <param name="transaction">The distributed transaction to enlist in. Must be active.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BlobOpenResult> OpenAsync(
        string key,
        BlobAccessMode mode,
        Transaction transaction,
        CancellationToken ct = default);

    /// <param name="loFd">The file descriptor returned by <see cref="OpenAsync"/>.</param>
    /// <param name="transaction">The distributed transaction to enlist in.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CloseAsync(
        int loFd,
        Transaction transaction,
        CancellationToken ct = default);

    /// <summary>Read up to <paramref name="count"/> bytes from the current position.</summary>
    /// <param name="loFd">The file descriptor returned by <see cref="OpenAsync"/>.</param>
    /// <param name="count">Maximum number of bytes to read.</param>
    /// <param name="transaction">The distributed transaction to enlist in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="BlobReadResult"/> where <c>BytesRead</c> may be less than <paramref name="count"/>; 0 indicates end-of-blob.</returns>
    Task<BlobReadResult> ReadAsync(
        int loFd,
        int count,
        Transaction transaction,
        CancellationToken ct = default);

    /// <summary>Write data at the current position and advance the offset.</summary>
    /// <param name="loFd">The file descriptor returned by <see cref="OpenAsync"/>.</param>
    /// <param name="data">The bytes to write.</param>
    /// <param name="transaction">The distributed transaction to enlist in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of bytes actually written.</returns>
    Task<int> WriteAsync(
        int loFd,
        byte[] data,
        Transaction transaction,
        CancellationToken ct = default);

    /// <summary>Reposition the read/write offset.</summary>
    /// <param name="loFd">The file descriptor returned by <see cref="OpenAsync"/>.</param>
    /// <param name="offset">Offset relative to <paramref name="origin"/>.</param>
    /// <param name="origin">Reference point (Begin, Current, or End).</param>
    /// <param name="transaction">The distributed transaction to enlist in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new absolute offset from the beginning of the blob.</returns>
    Task<long> SeekAsync(
        int loFd,
        long offset,
        SeekOrigin origin,
        Transaction transaction,
        CancellationToken ct = default);

    /// <param name="loFd">The file descriptor returned by <see cref="OpenAsync"/>.</param>
    /// <param name="length">New size in bytes.</param>
    /// <param name="transaction">The distributed transaction to enlist in.</param>
    /// <param name="ct">Cancellation token.</param>
    Task TruncateAsync(
        int loFd,
        long length,
        Transaction transaction,
        CancellationToken ct = default);
}
