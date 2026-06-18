using System.Collections.Concurrent;
using System.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Configuration;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Services;

/// <summary>
/// 事务协调器核心组件。
///
/// 职责：
/// 1. 管理 .NET CommittableTransaction 的生命周期
/// 2. 协调多个 Driver 的 Enlistment（延迟 enlistment）
/// 3. 执行 All or Nothing 策略（Commit / Rollback）
/// 4. 处理超时（由 CommittableTransaction 内建机制自动处理）
/// 5. 管理重试逻辑
///
/// 设计原则：
/// - 不持有 TransactionContext 或自定义状态枚举
/// - 并发保护下放到 Driver 层，Coordinator 不做串行化
/// - 超时由 CommittableTransaction 内建机制处理，不手写监控
/// - 所有公共方法接受 string transactionId（即 LocalIdentifier）为参数
/// </summary>
public sealed class TransactionCoordinator : IDisposable
{
    // 唯一的数据存储：transaction_id (LocalIdentifier) → 轻量入口
    private readonly ConcurrentDictionary<string, TransactionEntry> _entries = new();

    // 进行中的 Commit Task 注册表，供 ShutdownAsync 等待
    private readonly ConcurrentDictionary<string, Task<CommitResult>> _pendingCommits = new();

    private readonly IEnumerable<IResourceManager> _drivers;
    private readonly ILogger<TransactionCoordinator> _logger;
    private readonly CoordinatorOptions _options;
    private readonly SessionManager _sessionManager;
    private bool _disposed;

    private sealed record TransactionEntry(
        CommittableTransaction Transaction,
        TimeSpan OriginalTimeout);

    public TransactionCoordinator(
        IEnumerable<IResourceManager> drivers,
        SessionManager sessionManager,
        IOptions<CoordinatorOptions> options,
        ILogger<TransactionCoordinator> logger)
    {
        _drivers = drivers ?? throw new ArgumentNullException(nameof(drivers));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _sessionManager.OnSessionTimeout += OnSessionTimeout;
    }

    /// <summary>
    /// 开始一个新事务。
    /// 返回 <see cref="Transaction"/> 基类以保留扩展空间，
    /// ExpiresAt 一次性计算，后续不维护。
    /// </summary>
    public async Task<BeginTransactionResult> BeginTransactionAsync(
        TimeSpan? timeout = null,
        string? connectionId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var txTimeout = timeout ?? _options.DefaultTransactionTimeout;
        if (txTimeout > _options.MaxTransactionTimeout)
        {
            txTimeout = _options.MaxTransactionTimeout;
        }

        // CommittableTransaction 内建超时机制，超时自动触发 Rollback
        var committableTx = new CommittableTransaction(txTimeout);
        var localKey = committableTx.TransactionInformation.LocalIdentifier;

        var entry = new TransactionEntry(committableTx, txTimeout);
        _entries[localKey] = entry;

        // 创建会话
        var session = _sessionManager.CreateSession(committableTx, connectionId);

        // expires_at 一次性计算，仅用于 BeginTransactionResponse
        var expiresAt = DateTime.UtcNow + txTimeout;

        _logger.LogInformation(
            "Transaction '{LocalId}' started, timeout={Timeout}, session={SessionId}",
            localKey, txTimeout, session.SessionId);

        return new BeginTransactionResult(committableTx, expiresAt);
    }

    /// <summary>
    /// 提交事务（两阶段提交）。
    /// 内部由 CommittableTransaction.Commit() 触发 .NET DTC 协调。
    /// </summary>
    public async Task<CommitResult> CommitTransactionAsync(
        string transactionId,
        CancellationToken ct = default)
    {
        var entry = FindEntry(transactionId)
            ?? throw new InvalidOperationException($"Transaction '{transactionId}' not found or already completed.");

        _logger.LogInformation("Transaction '{LocalId}': starting two-phase commit", transactionId);

        // 将 Task 注册到 pending 表，供 ShutdownAsync 等待
        var commitTask = CommitCoreAsync(entry, transactionId, ct);
        _pendingCommits[transactionId] = commitTask;

        try
        {
            return await commitTask;
        }
        finally
        {
            _pendingCommits.TryRemove(transactionId, out _);
            CleanupTransaction(transactionId);
        }
    }

    private async Task<CommitResult> CommitCoreAsync(
        TransactionEntry entry,
        string transactionId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            // CommittableTransaction.Commit() 是同步的（.NET 约束），
            // 它在当前线程上触发所有 Enlisted Driver 的 Prepare → Commit 回调链。
            // 如果任一 Prepare 失败/抛出异常，.NET 自动通知所有已 Prepare 的 Driver 执行 Rollback。
            entry.Transaction.Commit();

            _logger.LogInformation("Transaction '{LocalId}': committed successfully", transactionId);
            return new CommitResult(CommitStatus.Committed, Array.Empty<DriverCommitResult>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction '{LocalId}': commit failed", transactionId);
            return new CommitResult(
                CommitStatus.RolledBack,
                Array.Empty<DriverCommitResult>(),
                ex.Message);
        }
    }

    /// <summary>
    /// 回滚事务。
    /// </summary>
    public async Task RollbackTransactionAsync(
        string transactionId,
        CancellationToken ct = default)
    {
        var entry = FindEntry(transactionId);

        if (entry == null)
        {
            _logger.LogWarning("Transaction '{LocalId}': not found or already completed, skipping rollback", transactionId);
            return;
        }

        ct.ThrowIfCancellationRequested();
        _logger.LogInformation("Transaction '{LocalId}': rolling back", transactionId);

        try
        {
            entry.Transaction.Rollback();
            _logger.LogInformation("Transaction '{LocalId}': rolled back", transactionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction '{LocalId}': rollback encountered an error", transactionId);
        }
        finally
        {
            CleanupTransaction(transactionId);
        }
    }

    /// <summary>
    /// 通过 transaction_id（即 <see cref="TransactionInformation.LocalIdentifier"/>）
    /// 查找对应的 <see cref="Transaction"/> 实例。
    /// 未找到时返回 null。
    /// </summary>
    public Transaction? FindTransaction(string transactionId)
    {
        return _entries.TryGetValue(transactionId, out var entry)
            ? entry.Transaction
            : null;
    }

    /// <summary>
    /// 查找提供指定能力的 Driver
    /// </summary>
    public TDriver? GetDriver<TDriver>() where TDriver : IResourceManager
    {
        return _drivers.OfType<TDriver>().FirstOrDefault();
    }

    /// <summary>
    /// 查找所有提供指定能力的 Driver
    /// </summary>
    public IEnumerable<TDriver> GetDrivers<TDriver>() where TDriver : IResourceManager
    {
        return _drivers.OfType<TDriver>();
    }

    /// <summary>
    /// 使用指定 Driver 类型在事务上下文中执行数据操作。
    /// 如果 Driver 支持事务，自动 Enlist 到当前事务。
    /// 操作回调中会传入当前 .NET Transaction 对象。
    /// 并发保护由 Driver 层自行负责。
    /// </summary>
    public async Task<TResult> ExecuteOnDriverAsync<TDriver, TResult>(
        string transactionId,
        Func<TDriver, Transaction, Task<TResult>> operation,
        CancellationToken ct = default)
        where TDriver : class, IResourceManager
        where TResult : class
    {
        var entry = FindEntry(transactionId)
            ?? throw new InvalidOperationException($"Transaction '{transactionId}' not found.");

        var driver = _drivers.OfType<TDriver>().FirstOrDefault()
            ?? throw new InvalidOperationException($"No driver of type {typeof(TDriver).Name} registered.");

        if (driver is ITransactionalResourceManager txDriver)
        {
            txDriver.Enlist(entry.Transaction);
        }

        return await operation(driver, entry.Transaction);
    }

    /// <summary>
    /// 使用指定能力接口在事务上下文中执行数据操作。
    /// <typeparamref name="TCapability"/> 是能力接口（如 <see cref="ISqlExecuteCapability"/>），
    /// 不需要继承 <see cref="IResourceManager"/>；内部自动查找同时实现两者的 Driver。
    /// 并发保护由 Driver 层自行负责。
    /// </summary>
    public async Task<TResult> ExecuteOnCapabilityAsync<TCapability, TResult>(
        string transactionId,
        Func<TCapability, Transaction, Task<TResult>> operation,
        CancellationToken ct = default)
        where TCapability : class
        where TResult : class
    {
        var entry = FindEntry(transactionId)
            ?? throw new InvalidOperationException($"Transaction '{transactionId}' not found.");

        var driver = _drivers.OfType<IResourceManager>()
            .OfType<TCapability>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No driver implementing {typeof(TCapability).Name} registered.");

        if (driver is ITransactionalResourceManager txDriver)
        {
            txDriver.Enlist(entry.Transaction);
        }

        return await operation(driver, entry.Transaction);
    }

    /// <summary>
    /// 处理会话超时 — 自动回滚关联事务
    /// </summary>
    private void OnSessionTimeout(SessionContext session)
    {
        var txId = session.TransactionLocalIdentifier;
        var entry = FindEntry(txId);
        if (entry == null) return;

        _logger.LogWarning(
            "Session {SessionId} timed out, rolling back transaction '{LocalId}'",
            session.SessionId, txId);

        try
        {
            entry.Transaction.Rollback();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error rolling back timed-out transaction '{LocalId}'", txId);
        }

        CleanupTransaction(txId);
    }

    /// <summary>
    /// 清理事务资源并移除活跃记录
    /// </summary>
    private void CleanupTransaction(string transactionId)
    {
        if (_entries.TryRemove(transactionId, out var entry))
        {
            try { entry.Transaction.Dispose(); }
            catch { /* 忽略释放时的异常 */ }
        }
    }

    /// <summary>
    /// 通过 transaction_id 查找事务入口。
    /// 字典以 <see cref="TransactionInformation.LocalIdentifier"/> 为键，O(1) 查找。
    /// </summary>
    private TransactionEntry? FindEntry(string transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        return _entries.TryGetValue(transactionId, out var entry) ? entry : null;
    }

    /// <summary>
    /// 获取当前活跃事务数量
    /// </summary>
    public int ActiveTransactionCount => _entries.Count;

    /// <summary>
    /// 优雅关闭：
    /// 1. 等待所有进行中的 Commit Task 完成（受 gracePeriod 约束）
    /// 2. 回滚其余活跃事务
    /// </summary>
    public async Task ShutdownAsync(TimeSpan gracePeriod)
    {
        _logger.LogInformation(
            "Coordinator shutting down, {Count} active transactions, {Pending} pending commits",
            _entries.Count, _pendingCommits.Count);

        // 第一步：等待进行中的提交完成
        if (_pendingCommits.Count > 0)
        {
            using var cts = new CancellationTokenSource(gracePeriod);
            try
            {
                var pendingTasks = _pendingCommits.Values.ToArray();
                await Task.WhenAll(pendingTasks).WaitAsync(cts.Token);
                _logger.LogInformation("All pending commits completed");
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Graceful shutdown: timeout waiting for pending commits");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Graceful shutdown: cancelled while waiting for pending commits");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Graceful shutdown: error while waiting for pending commits");
            }
        }

        // 第二步：回滚剩余活跃事务
        foreach (var (txId, entry) in _entries)
        {
            _logger.LogWarning(
                "Graceful shutdown: rolling back transaction {TransactionId}", txId);
            try
            {
                entry.Transaction.Rollback();
            }
            catch { /* 回滚阶段的异常不阻塞整体流程 */ }
            CleanupTransaction(txId);
        }

        _logger.LogInformation("Coordinator shutdown complete");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionManager.OnSessionTimeout -= OnSessionTimeout;

        foreach (var (_, entry) in _entries)
        {
            try { entry.Transaction.Rollback(); } catch { }
            try { entry.Transaction.Dispose(); } catch { }
        }

        _entries.Clear();
        _pendingCommits.Clear();
    }
}
