using System.Collections.Concurrent;
using System.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Configuration;
using Adaptor.Coordinator.Models;

namespace Adaptor.Coordinator.Services;

/// <summary>
/// Coordinates distributed transactions across multiple resource managers with two-phase commit.
/// </summary>
/// <remarks>
/// Responsibilities:
/// 1. Manage .NET CommittableTransaction lifecycle
/// 2. Coordinate multi-driver enlistment (deferred enlistment)
/// 3. Execute All-or-Nothing commit/rollback strategy
/// 4. Handle timeouts via CommittableTransaction built-in mechanism
/// 5. Manage retry logic
///
/// Design principles:
/// - No TransactionContext or custom state enum
/// - Concurrency protection delegated to driver layer
/// - All public methods accept string transactionId (LocalIdentifier)
/// </remarks>
public sealed class TransactionCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, TransactionEntry> _entries = new();

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
    /// Begin a new distributed transaction.
    /// </summary>
    /// <param name="timeout">Optional timeout; capped at <see cref="CoordinatorOptions.MaxTransactionTimeout"/>.</param>
    /// <param name="connectionId">Optional gRPC connection ID for session tracking.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing the <see cref="Transaction"/> and its expiration time.</returns>
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

        var committableTx = new CommittableTransaction(txTimeout);
        var localKey = committableTx.TransactionInformation.LocalIdentifier;

        var entry = new TransactionEntry(committableTx, txTimeout);
        _entries[localKey] = entry;

        var session = _sessionManager.CreateSession(committableTx, connectionId);

        var expiresAt = DateTime.UtcNow + txTimeout;

        _logger.LogInformation(
            "Transaction '{LocalId}' started, timeout={Timeout}, session={SessionId}",
            localKey, txTimeout, session.SessionId);

        return new BeginTransactionResult(committableTx, expiresAt);
    }

    /// <summary>
    /// Begin a long-lived transaction for Blob Streaming.
    /// </summary>
    /// <remarks>Uses an independent timeout to avoid constraining streaming operations.</remarks>
    /// <param name="timeout">Optional timeout; capped at <see cref="CoordinatorOptions.MaxBlobStreamTransactionTimeout"/>.</param>
    /// <param name="connectionId">Optional gRPC connection ID for session tracking.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing the <see cref="Transaction"/> and its expiration time.</returns>
    public async Task<BeginTransactionResult> BeginBlobStreamTransactionAsync(
        TimeSpan? timeout = null,
        string? connectionId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var txTimeout = timeout ?? _options.BlobStreamTransactionTimeout;
        if (txTimeout > _options.MaxBlobStreamTransactionTimeout)
        {
            txTimeout = _options.MaxBlobStreamTransactionTimeout;
        }

        var committableTx = new CommittableTransaction(txTimeout);
        var localKey = committableTx.TransactionInformation.LocalIdentifier;

        var entry = new TransactionEntry(committableTx, txTimeout);
        _entries[localKey] = entry;

        var session = _sessionManager.CreateSession(committableTx, connectionId);

        var expiresAt = DateTime.UtcNow + txTimeout;

        _logger.LogInformation(
            "BlobStream transaction '{LocalId}' started, timeout={Timeout}, session={SessionId}",
            localKey, txTimeout, session.SessionId);

        return new BeginTransactionResult(committableTx, expiresAt);
    }

    /// <summary>
    /// Commit a transaction using two-phase commit.
    /// </summary>
    /// <param name="transactionId">The transaction's <see cref="TransactionInformation.LocalIdentifier"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Overall commit result including per-driver outcomes.</returns>
    /// <exception cref="InvalidOperationException">Transaction not found or already completed.</exception>
    public async Task<CommitResult> CommitTransactionAsync(
        string transactionId,
        CancellationToken ct = default)
    {
        var entry = FindEntry(transactionId)
            ?? throw new InvalidOperationException($"Transaction '{transactionId}' not found or already completed.");

        _logger.LogInformation("Transaction '{LocalId}': starting two-phase commit", transactionId);

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
    /// Roll back a transaction. No-op if the transaction is not found.
    /// </summary>
    /// <param name="transactionId">The transaction's <see cref="TransactionInformation.LocalIdentifier"/>.</param>
    /// <param name="ct">Cancellation token.</param>
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

    public Transaction? FindTransaction(string transactionId)
    {
        return _entries.TryGetValue(transactionId, out var entry)
            ? entry.Transaction
            : null;
    }

    /// <summary>
    /// Get the first driver implementing <typeparamref name="TDriver"/>.
    /// </summary>
    /// <returns>The driver, or <c>null</c> if not found.</returns>
    public TDriver? GetDriver<TDriver>() where TDriver : IResourceManager
    {
        return _drivers.OfType<TDriver>().FirstOrDefault();
    }

    public IEnumerable<TDriver> GetDrivers<TDriver>() where TDriver : IResourceManager
    {
        return _drivers.OfType<TDriver>();
    }

    /// <summary>
    /// Execute an operation on a specific driver within a transaction.
    /// Auto-enlists if the driver supports <see cref="ITransactionalResourceManager"/>.
    /// </summary>
    /// <remarks>Concurrency protection is the driver's responsibility.</remarks>
    /// <param name="transactionId">The transaction's LocalIdentifier.</param>
    /// <param name="operation">Async callback receiving the driver and transaction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <typeparam name="TDriver">Driver type implementing <see cref="IResourceManager"/>.</typeparam>
    /// <typeparam name="TResult">Operation result type.</typeparam>
    /// <returns>The result of <paramref name="operation"/>.</returns>
    /// <exception cref="InvalidOperationException">Transaction or driver not found.</exception>
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
    /// Execute an operation using a capability interface within a transaction.
    /// <typeparamref name="TCapability"/> may be any interface; the method finds
    /// a driver implementing both it and <see cref="IResourceManager"/>.
    /// </summary>
    /// <remarks>Concurrency protection is the driver's responsibility.</remarks>
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

    private void CleanupTransaction(string transactionId)
    {
        if (_entries.TryRemove(transactionId, out var entry))
        {
            try { entry.Transaction.Dispose(); }
            catch { }
        }
    }

    private TransactionEntry? FindEntry(string transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        return _entries.TryGetValue(transactionId, out var entry) ? entry : null;
    }

    public int ActiveTransactionCount => _entries.Count;

    /// <summary>
    /// Gracefully shut down the coordinator:
    /// 1. Wait for in-flight commit tasks to complete (bounded by gracePeriod)
    /// 2. Roll back remaining active transactions
    /// </summary>
    public async Task ShutdownAsync(TimeSpan gracePeriod)
    {
        _logger.LogInformation(
            "Coordinator shutting down, {Count} active transactions, {Pending} pending commits",
            _entries.Count, _pendingCommits.Count);

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

        foreach (var (txId, entry) in _entries)
        {
            _logger.LogWarning(
                "Graceful shutdown: rolling back transaction {TransactionId}", txId);
            try
            {
                entry.Transaction.Rollback();
            }
            catch { }
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
