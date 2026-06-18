namespace Adaptor.Coordinator.Models;

/// <summary>BLOB 上传请求</summary>
public sealed record BlobUploadRequest(
    string Key,
    byte[] Data,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>BLOB 上传结果</summary>
public sealed record BlobUploadResult(
    string Key,
    long Size,
    string? ErrorMessage = null);

/// <summary>BLOB 下载请求</summary>
public sealed record BlobDownloadRequest(string Key);

/// <summary>BLOB 下载结果</summary>
public sealed record BlobDownloadResult(
    string Key,
    byte[] Data,
    string? ContentType,
    string? ErrorMessage = null);
