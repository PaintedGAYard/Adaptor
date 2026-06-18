namespace Adaptor.Coordinator.Models;

/// <summary>
/// BLOB 随机访问打开结果
/// </summary>
public sealed record BlobOpenResult(
    /// <summary>PostgreSQL Large Object 文件描述符</summary>
    int LoFd,
    /// <summary>已有 BLOB 的大小（新建时为 0）</summary>
    long BlobSize,
    /// <summary>错误消息，成功时为 null</summary>
    string? ErrorMessage = null);

/// <summary>
/// BLOB 随机访问读取结果
/// </summary>
public sealed record BlobReadResult(
    /// <summary>读取到的数据</summary>
    byte[] Data,
    /// <summary>实际读取的字节数（0 表示已到末尾）</summary>
    int BytesRead,
    /// <summary>错误消息，成功时为 null</summary>
    string? ErrorMessage = null);
