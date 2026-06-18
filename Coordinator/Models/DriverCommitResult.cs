using System.Transactions;

namespace Adaptor.Coordinator.Models;

/// <param name="DriverName">Logical driver name (e.g. the driver's <see cref=\"IResourceManager.Name\"/>).</param>
/// <param name="Success">Whether this driver's commit phase succeeded.</param>
/// <param name="RetryCount">Number of retry attempts made before this outcome.</param>
/// <param name="ErrorMessage">Null on success; describes the failure otherwise.</param>
public sealed record DriverCommitResult(
    string DriverName,
    bool Success,
    int RetryCount,
    string? ErrorMessage = null);

/// <param name="Status">Overall commit status (<see cref="CommitStatus"/>).</param>
/// <param name="DriverResults">Per-driver results; always present.</param>
/// <param name="ErrorMessage">Aggregate error message; set on partial or failed commits.</param>
public sealed record CommitResult(
    CommitStatus Status,
    IReadOnlyList<DriverCommitResult> DriverResults,
    string? ErrorMessage = null);

/// <param name="Transaction">The committed <see cref="System.Transactions.Transaction"/> instance.</param>
/// <param name="ExpiresAt">UTC timestamp after which the transaction is considered expired.</param>
public sealed record BeginTransactionResult(
    Transaction Transaction,
    DateTime ExpiresAt);
