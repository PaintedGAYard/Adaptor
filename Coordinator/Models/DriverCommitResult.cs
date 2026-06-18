using System.Transactions;

namespace Adaptor.Coordinator.Models;

/// <summary>
/// 各 Driver 的提交结果记录
/// </summary>
public sealed record DriverCommitResult(
    string DriverName,
    bool Success,
    int RetryCount,
    string? ErrorMessage = null);

/// <summary>
/// 提交操作的最终结果
/// </summary>
public sealed record CommitResult(
    CommitStatus Status,
    IReadOnlyList<DriverCommitResult> DriverResults,
    string? ErrorMessage = null);

/// <summary>
/// 开始事务的结果。
/// 返回 <see cref="Transaction"/> 基类以保留扩展空间，
/// expires_at 一次性计算，后续不维护。
/// </summary>
public sealed record BeginTransactionResult(
    Transaction Transaction,
    DateTime ExpiresAt);
