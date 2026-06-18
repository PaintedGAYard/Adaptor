using System.Collections.Concurrent;
using System.Data.Common;
using System.Transactions;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Adaptor.Driver.Postgre;

/// <summary>
/// PostgreSQL SQL driver providing Execute/Query capabilities with
/// .NET distributed transaction support via <see cref="ITransactionalResourceManager.Enlist"/>.
/// </summary>
public sealed class PostgreSqlDriver :
    IResourceManager,
    ITransactionalResourceManager,
    IRelationalExecuteCapability,
    IRelationalQueryCapability,
    IHealthCheckCapability,
    IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<PostgreSqlDriver>? _logger;
    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _txLocks = new();
    private bool _disposed;

    public string Name => "PostgreSQL";
    public ResourceType ResourceType => ResourceType.Sql;
    public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();

    public PostgreSqlDriver(string connectionString, ILogger<PostgreSqlDriver>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _logger = logger;
    }

    #region ITransactionalResourceManager

    public void Enlist(Transaction transaction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var txId = transaction.TransactionInformation.LocalIdentifier;

        if (_connections.ContainsKey(txId))
        {
            _logger?.LogDebug("PostgreSqlDriver already enlisted in transaction {TxId}", txId);
            return;
        }

        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        var localTransaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);

        var entry = new ConnectionEntry(connection, localTransaction, transaction);
        if (!_connections.TryAdd(txId, entry))
        {
            // Another caller beat us to it — clean up and return
            _logger?.LogDebug("PostgreSqlDriver already enlisted in transaction {TxId} (race)", txId);
            localTransaction.Dispose();
            connection.Dispose();
            return;
        }

        // Register a per-transaction enlistment handler so each transaction's
        // Prepare/Commit/Rollback lifecycle is isolated.
        transaction.EnlistVolatile(new NpgsqlEnlistmentHandler(this, txId), EnlistmentOptions.None);

        _logger?.LogDebug("PostgreSqlDriver enlisted in transaction {TxId}", txId);
    }

    #endregion

    #region IRelationalExecuteCapability

    public async Task<RelationalExecuteResult> ExecuteAsync(RelationalExecuteRequest request, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        var txId = transaction.TransactionInformation.LocalIdentifier;
        var gate = GetOrCreateTxLock(txId);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        var start = DateTime.UtcNow;
        try
        {
            var entry = GetEntry(transaction);

            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = request.Command;
            cmd.Transaction = entry.LocalTransaction;

            if (request.Parameters is { Count: > 0 })
            {
                AddParameters(cmd, request.Parameters);
            }

            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var duration = DateTime.UtcNow - start;

            _logger?.LogDebug("SQL EXECUTE [{TxId}] affected {Count} rows in {Duration:F2}ms",
                txId, affected, duration.TotalMilliseconds);

            return new RelationalExecuteResult(affected, duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - start;
            _logger?.LogError(ex, "PostgreSqlDriver ExecuteAsync failed for transaction {TxId}", txId);
            return new RelationalExecuteResult(0, duration, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    #endregion

    #region IRelationalQueryCapability

    public async Task<RelationalQueryResult> QueryAsync(RelationalQueryRequest request, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);

        var txId = transaction.TransactionInformation.LocalIdentifier;
        var gate = GetOrCreateTxLock(txId);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        var start = DateTime.UtcNow;
        try
        {
            var entry = GetEntry(transaction);

            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = request.Command;
            cmd.Transaction = entry.LocalTransaction;

            if (request.Parameters is { Count: > 0 })
            {
                AddParameters(cmd, request.Parameters);
            }

            var rows = new List<Dictionary<string, object?>>();

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row[reader.GetName(i)] = value == DBNull.Value ? null : value;
                }
                rows.Add(row);
            }

            var duration = DateTime.UtcNow - start;

            _logger?.LogDebug("SQL QUERY  [{TxId}] returned {Count} rows in {Duration:F2}ms",
                txId, rows.Count, duration.TotalMilliseconds);

            return new RelationalQueryResult(rows, duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - start;
            _logger?.LogError(ex, "PostgreSqlDriver QueryAsync failed for transaction {TxId}", txId);
            return new RelationalQueryResult(Array.Empty<IDictionary<string, object?>>(), duration, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    #endregion

    #region IHealthCheckCapability

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PostgreSqlDriver health check failed");
            return false;
        }
    }

    #endregion

    #region Internal helpers

    private ConnectionEntry GetEntry(Transaction transaction)
    {
        var txId = transaction.TransactionInformation.LocalIdentifier;

        if (_connections.TryGetValue(txId, out var entry))
            return entry;

        throw new InvalidOperationException(
            $"Transaction (LocalIdentifier={txId}) is not enlisted with this driver. " +
            "Enlist() must be called before data operations.");
    }

    private static void AddParameters(DbCommand cmd, IReadOnlyList<RelationalParameter> parameters)
    {
        foreach (var param in parameters)
        {
            var dbParam = cmd.CreateParameter();
            dbParam.ParameterName = param.Name;
            dbParam.Value = param.Value ?? DBNull.Value;
            cmd.Parameters.Add(dbParam);
        }
    }

    /// <summary>Release resources for a completed transaction.</summary>
    internal void RemoveEntry(string txId)
    {
        if (_connections.TryRemove(txId, out var entry))
        {
            _logger?.LogDebug("PostgreSqlDriver removing transaction {TxId}", txId);
            entry.Dispose();
        }

        if (_txLocks.TryRemove(txId, out var gate))
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// Get or create a per-transaction concurrency lock.
    /// Serialises operations within the same transaction to avoid concurrent Npgsql use.
    /// </summary>
    private SemaphoreSlim GetOrCreateTxLock(string txId)
    {
        return _txLocks.GetOrAdd(txId, _ => new SemaphoreSlim(1, 1));
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, entry) in _connections)
        {
            entry.Dispose();
        }
        _connections.Clear();

        foreach (var (_, gate) in _txLocks)
        {
            gate.Dispose();
        }
        _txLocks.Clear();
    }

    #endregion

    #region Nested types

    private sealed record ConnectionEntry : IDisposable
    {
        public NpgsqlConnection Connection { get; }
        public NpgsqlTransaction LocalTransaction { get; }

        public ConnectionEntry(NpgsqlConnection connection, NpgsqlTransaction localTransaction, Transaction transaction)
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

    /// <summary>Handles Prepare/Commit/Rollback for a single distributed transaction.</summary>
    /// <remarks>Each transaction gets its own handler instance for correct routing.</remarks>
    private sealed class NpgsqlEnlistmentHandler : IEnlistmentNotification
    {
        private readonly PostgreSqlDriver _driver;
        private readonly string _txId;

        public NpgsqlEnlistmentHandler(PostgreSqlDriver driver, string txId)
        {
            _driver = driver;
            _txId = txId;
        }

        void IEnlistmentNotification.Prepare(PreparingEnlistment preparingEnlistment)
        {
            try
            {
                _driver._logger?.LogDebug("PostgreSqlDriver [{TxId}] Prepare: voting Prepared", _txId);
                preparingEnlistment.Prepared();
            }
            catch (Exception ex)
            {
                _driver._logger?.LogError(ex, "PostgreSqlDriver [{TxId}] Prepare: force rollback", _txId);
                preparingEnlistment.ForceRollback();
            }
        }

        void IEnlistmentNotification.Commit(Enlistment enlistment)
        {
            try
            {
                _driver._logger?.LogDebug("PostgreSqlDriver [{TxId}] Commit: committing local transaction", _txId);

                if (_driver._connections.TryGetValue(_txId, out var entry))
                {
                    entry.LocalTransaction.Commit();
                    _driver._logger?.LogDebug("PostgreSqlDriver [{TxId}] local transaction committed", _txId);
                }
            }
            catch (Exception ex)
            {
                _driver._logger?.LogError(ex, "PostgreSqlDriver [{TxId}] Commit failed", _txId);
            }
            finally
            {
                _driver.RemoveEntry(_txId);
                enlistment.Done();
            }
        }

        void IEnlistmentNotification.Rollback(Enlistment enlistment)
        {
            try
            {
                _driver._logger?.LogDebug("PostgreSqlDriver [{TxId}] Rollback: rolling back local transaction", _txId);

                if (_driver._connections.TryGetValue(_txId, out var entry))
                {
                    entry.LocalTransaction.Rollback();
                    _driver._logger?.LogDebug("PostgreSqlDriver [{TxId}] local transaction rolled back", _txId);
                }
            }
            catch (Exception ex)
            {
                _driver._logger?.LogError(ex, "PostgreSqlDriver [{TxId}] Rollback failed", _txId);
            }
            finally
            {
                _driver.RemoveEntry(_txId);
                enlistment.Done();
            }
        }

        void IEnlistmentNotification.InDoubt(Enlistment enlistment)
        {
            _driver._logger?.LogWarning("PostgreSqlDriver [{TxId}] InDoubt: transaction outcome unknown", _txId);
            _driver.RemoveEntry(_txId);
            enlistment.Done();
        }
    }

    #endregion
}
