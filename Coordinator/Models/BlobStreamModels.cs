namespace Adaptor.Coordinator.Models;

/// <param name="LoFd">File descriptor returned by <c>OpenAsync</c> — use in subsequent read/write/seek calls.</param>
/// <param name="BlobSize">Size of the existing blob; 0 for newly created.</param>
/// <param name="ErrorMessage">Null on success; non-null on failure.</param>
public sealed record BlobOpenResult(
    int LoFd,
    long BlobSize,
    string? ErrorMessage = null);

/// <param name="Data">The bytes read.</param>
/// <param name="BytesRead">Number of bytes returned; 0 indicates end-of-blob.</param>
/// <param name="ErrorMessage">Null on success; non-null on failure.</param>
public sealed record BlobReadResult(
    byte[] Data,
    int BytesRead,
    string? ErrorMessage = null);
