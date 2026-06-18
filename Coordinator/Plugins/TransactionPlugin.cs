using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Adaptor.Coordinator.Models;
using Adaptor.Coordinator.Services;

namespace Adaptor.Coordinator.Plugins;

/// <summary>
/// SK Plugin — 事务生命周期管理。
/// 包装 <see cref="TransactionCoordinator"/> 的 Begin / Commit / Rollback。
/// </summary>
public sealed class TransactionPlugin
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ILogger<TransactionPlugin> _logger;

    public TransactionPlugin(TransactionCoordinator coordinator, ILogger<TransactionPlugin> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    [KernelFunction("begin_transaction")]
    [Description("Begin a new distributed transaction")]
    public async Task<BeginTransactionResult> BeginTransactionAsync(
        [Description("Optional timeout (e.g. 00:01:30 for 90s)")] TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        return await _coordinator.BeginTransactionAsync(timeout, ct: ct);
    }

    [KernelFunction("commit_transaction")]
    [Description("Commit a transaction (two-phase commit)")]
    public async Task<CommitResult> CommitTransactionAsync(
        [Description("Transaction ID to commit")] string transactionId,
        CancellationToken ct = default)
    {
        return await _coordinator.CommitTransactionAsync(transactionId, ct);
    }

    [KernelFunction("rollback_transaction")]
    [Description("Roll back a transaction")]
    public async Task RollbackTransactionAsync(
        [Description("Transaction ID to roll back")] string transactionId,
        CancellationToken ct = default)
    {
        await _coordinator.RollbackTransactionAsync(transactionId, ct);
    }
}
