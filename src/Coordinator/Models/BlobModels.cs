namespace Adaptor.Coordinator.Models;

/// <param name="Key">The unique blob identifier.</param>
/// <param name="Data">Raw bytes to store.</param>
/// <param name="ContentType">Optional MIME type; null means unspecified.</param>
/// <param name="Metadata">Optional key-value pairs stored alongside the blob.</param>
public sealed record BlobUploadRequest(
    string Key,
    byte[] Data,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <param name="Key">Echo of the request key.</param>
/// <param name="Size">Stored size in bytes.</param>
/// <param name="ErrorMessage">Null on success; non-null on failure (other fields may be invalid).</param>
public sealed record BlobUploadResult(
    string Key,
    long Size,
    string? ErrorMessage = null);

/// <summary>Download a blob by its key (same key used in upload).</summary>
/// <param name="Key">The unique blob identifier.</param>
public sealed record BlobDownloadRequest(string Key);

/// <param name="Key">Echo of the requested key.</param>
/// <param name="Data">Raw bytes as stored.</param>
/// <param name="ContentType">The MIME type stored with the blob; null if unspecified.</param>
/// <param name="ErrorMessage">Null on success; non-null on failure.</param>
public sealed record BlobDownloadResult(
    string Key,
    byte[] Data,
    string? ContentType,
    string? ErrorMessage = null);
