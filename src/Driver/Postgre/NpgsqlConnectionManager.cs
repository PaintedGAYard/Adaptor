using System.Collections.Concurrent;
using System.Transactions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Adaptor.Driver.Postgre;

/// <summary>
/// Manages Npgsql connections and local transactions for a single driver instance.
/// Each driver creates its own manager; the manager is not shared across drivers.
/// </summary>
/// <remarks>
/// Responsibilities:
/// 1. Create and track Npgsql connections per .NET Transaction (via LocalIdentifier)
/// 2. Enlist the driver as a volatile resource manager in each transaction
/// 3. Provide thread-safe per-transaction locking (optional, used by the driver)
/// 4. Clean up all resources on dispose
/// </remarks>
public sealed class NpgsqlConnectionManager : IDisposable
{
    private readonly string? _connectionString;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly string _driverName;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _txLocks = new();
    private bool _disposed;

    public NpgsqlConnectionManager(string connectionString, string driverName, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _driverName = driverName;
        _logger = logger;
    }

    public NpgsqlConnectionManager(NpgsqlDataSource dataSource, string driverName, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _driverName = driverName;
        _logger = logger;
    }

    /// <summary>Enlist this manager in the specified transaction.</summary>
    /// <remarks>
    /// Creates a new NpgsqlConnection + NpgsqlTransaction and registers a volatile
    /// enlistment handler. Idempotent: if already enlisted for the given transaction,
    /// this is a no-op.
    /// </remarks>
    public void Enlist(Transaction transaction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var txId = transaction.TransactionInformation.LocalIdentifier;

        if (_connections.ContainsKey(txId))
        {
            _logger?.LogDebug("{Driver} already enlisted in transaction {TxId}", _driverName, txId);
            return;
        }

        var connection = _dataSource != null
            ? _dataSource.CreateConnection()
            : new NpgsqlConnection(_connectionString);
        connection.Open();
        var localTransaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);

        var entry = new ConnectionEntry(connection, localTransaction);
        if (!_connections.TryAdd(txId, entry))
        {
            localTransaction.Dispose();
            connection.Dispose();
            return;
        }

        transaction.EnlistVolatile(
            new NpgsqlEnlistmentHandler(this, txId, _driverName, _logger),
            EnlistmentOptions.None);

        _logger?.LogDebug("{Driver} enlisted in transaction {TxId}", _driverName, txId);
    }

    /// <summary>Get the connection entry for a transaction. Throws if not enlisted.</summary>
    public ConnectionEntry GetEntry(Transaction transaction)
    {
        var txId = transaction.TransactionInformation.LocalIdentifier;

        if (_connections.TryGetValue(txId, out var entry))
            return entry;

        throw new InvalidOperationException(
            $"Transaction (LocalIdentifier={txId}) is not enlisted with {_driverName}. " +
            "Enlist() must be called before data operations.");
    }

    /// <summary>Try to get a connection entry by transaction ID. Returns null if not found.</summary>
    public bool TryGetEntry(string txId, out ConnectionEntry? entry)
    {
        return _connections.TryGetValue(txId, out entry);
    }

    /// <summary>Remove and dispose the connection entry for a completed transaction.</summary>
    public void RemoveEntry(string txId)
    {
        if (_connections.TryRemove(txId, out var entry))
        {
            _logger?.LogDebug("{Driver} removing transaction {TxId}", _driverName, txId);
            entry.Dispose();
        }

        if (_txLocks.TryRemove(txId, out var gate))
        {
            gate.Dispose();
        }
    }

    /// <summary>Get or create a per-transaction semaphore for serialising operations.</summary>
    public SemaphoreSlim GetOrCreateTxLock(string txId)
    {
        return _txLocks.GetOrAdd(txId, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>Check reachability of the PostgreSQL server.</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = _dataSource != null
                ? _dataSource.CreateConnection()
                : new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{Driver} health check failed", _driverName);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var entry in _connections.Values)
        {
            entry.Dispose();
        }
        _connections.Clear();

        foreach (var gate in _txLocks.Values)
        {
            gate.Dispose();
        }
        _txLocks.Clear();
    }

    /// <summary>Per-transaction connection state.</summary>
    public sealed record ConnectionEntry : IDisposable
    {
        public NpgsqlConnection Connection { get; }
        public NpgsqlTransaction LocalTransaction { get; }

        public ConnectionEntry(NpgsqlConnection connection, NpgsqlTransaction localTransaction)
        {
            Connection = connection;
            LocalTransaction = localTransaction;
        }

        public void Dispose()
        {
            try { LocalTransaction.Dispose(); } catch { }
            try { Connection.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Handles Prepare/Commit/Rollback for a single distributed transaction.
    /// A new handler instance is created per transaction.
    /// </summary>
    private sealed class NpgsqlEnlistmentHandler : IEnlistmentNotification
    {
        private readonly NpgsqlConnectionManager _manager;
        private readonly string _txId;
        private readonly string _driverName;
        private readonly ILogger? _logger;

        public NpgsqlEnlistmentHandler(NpgsqlConnectionManager manager, string txId, string driverName, ILogger? logger)
        {
            _manager = manager;
            _txId = txId;
            _driverName = driverName;
            _logger = logger;
        }

        void IEnlistmentNotification.Prepare(PreparingEnlistment preparingEnlistment)
        {
            try
            {
                _logger?.LogDebug("{Driver} [{TxId}] Prepare: voting Prepared", _driverName, _txId);
                preparingEnlistment.Prepared();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "{Driver} [{TxId}] Prepare: force rollback", _driverName, _txId);
                preparingEnlistment.ForceRollback();
            }
        }

        void IEnlistmentNotification.Commit(Enlistment enlistment)
        {
            try
            {
                _logger?.LogDebug("{Driver} [{TxId}] Commit: committing local transaction", _driverName, _txId);

                if (_manager.TryGetEntry(_txId, out var entry) && entry != null)
                {
                    entry.LocalTransaction.Commit();
                    _logger?.LogDebug("{Driver} [{TxId}] local transaction committed", _driverName, _txId);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "{Driver} [{TxId}] Commit failed", _driverName, _txId);
            }
            finally
            {
                _manager.RemoveEntry(_txId);
                enlistment.Done();
            }
        }

        void IEnlistmentNotification.Rollback(Enlistment enlistment)
        {
            try
            {
                _logger?.LogDebug("{Driver} [{TxId}] Rollback: rolling back local transaction", _driverName, _txId);

                if (_manager.TryGetEntry(_txId, out var entry) && entry != null)
                {
                    entry.LocalTransaction.Rollback();
                    _logger?.LogDebug("{Driver} [{TxId}] local transaction rolled back", _driverName, _txId);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "{Driver} [{TxId}] Rollback failed", _driverName, _txId);
            }
            finally
            {
                _manager.RemoveEntry(_txId);
                enlistment.Done();
            }
        }

        void IEnlistmentNotification.InDoubt(Enlistment enlistment)
        {
            _logger?.LogWarning("{Driver} [{TxId}] InDoubt: transaction outcome unknown", _driverName, _txId);
            _manager.RemoveEntry(_txId);
            enlistment.Done();
        }
    }
}
