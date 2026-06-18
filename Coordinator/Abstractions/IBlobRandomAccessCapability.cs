using System.Transactions;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// BLOB 打开模式
/// </summary>
public enum BlobAccessMode
{
    /// <summary>打开已有 blob 进行读取</summary>
    Read = 1,
    /// <summary>打开已有 blob 进行写入（从 offset 0 开始）</summary>
    Write = 2,
    /// <summary>打开已有 blob 进行读写</summary>
    ReadWrite = 3,
    /// <summary>创建新 blob，key 已存在则报错</summary>
    Create = 4,
    /// <summary>创建新 blob，key 存在则替换</summary>
    CreateOrReplace = 5,
    /// <summary>打开已有 blob 并在末尾追加</summary>
    Append = 6,
}

/// <summary>
/// BLOB 随机访问能力。
/// 提供类似文件句柄的 Seek/Read/Write/Close 语义，
/// 基于 PostgreSQL Large Object API 实现。
/// 此接口与 IBlobUploadCapability / IBlobDownloadCapability 正交，
/// 专为流式随机访问场景设计。
/// </summary>
public interface IBlobRandomAccessCapability
{
    /// <summary>
    /// 打开一个 BLOB 用于随机访问。
    /// 返回底层 Large Object 的文件描述符（fd）。
    /// </summary>
    Task<BlobOpenResult> OpenAsync(
        string key,
        BlobAccessMode mode,
        Transaction transaction,
        CancellationToken ct = default);

    /// <summary>
    /// 关闭一个先前打开的 Large Object 文件描述符。
    /// </summary>
    Task CloseAsync(
        int loFd,
        Transaction transaction,
        CancellationToken ct = default);

    /// <summary>
    /// 从当前偏移量读取指定字节数的数据。
    /// </summary>
    Task<BlobReadResult> ReadAsync(
        int loFd,
        int count,
        Transaction transaction,
        CancellationToken ct = default);

    /// <summary>
    /// 在当前位置写入数据，偏移量自动前进。
    /// 返回实际写入的字节数。
    /// </summary>
    Task<int> WriteAsync(
        int loFd,
        byte[] data,
        Transaction transaction,
        CancellationToken ct = default);

    /// <summary>
    /// 移动读写位置。参照 <see cref="System.IO.SeekOrigin"/> 语义。
    /// 返回新的绝对偏移量。
    /// </summary>
    Task<long> SeekAsync(
        int loFd,
        long offset,
        SeekOrigin origin,
        Transaction transaction,
        CancellationToken ct = default);

    /// <summary>
    /// 将 Large Object 截断到指定长度。
    /// </summary>
    Task TruncateAsync(
        int loFd,
        long length,
        Transaction transaction,
        CancellationToken ct = default);
}
