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
    private readonly NpgsqlConnectionManager _connectionManager;
    private readonly ILogger<PostgreSqlDriver>? _logger;
    private bool _disposed;

    public string Name => "PostgreSQL";
    public ResourceType ResourceType => ResourceType.Sql;
    public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();

    public PostgreSqlDriver(string connectionString, ILogger<PostgreSqlDriver>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionManager = new NpgsqlConnectionManager(connectionString, Name, logger);
        _logger = logger;
    }

    /// <summary>Create with an NpgsqlDataSource (recommended for connection pooling).</summary>
    public PostgreSqlDriver(NpgsqlDataSource dataSource, ILogger<PostgreSqlDriver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _connectionManager = new NpgsqlConnectionManager(dataSource, Name, logger);
        _logger = logger;
    }

    #region ITransactionalResourceManager

    public void Enlist(Transaction transaction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _connectionManager.Enlist(transaction);
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
        try
        {
            // Contract validation — fails fast (InvalidOperationException) if not enlisted
            var entry = GetEntry(transaction);

            var start = DateTime.UtcNow;
            try
            {
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
        try
        {
            // Contract validation — fails fast (InvalidOperationException) if not enlisted
            var entry = GetEntry(transaction);

            var start = DateTime.UtcNow;
            try
            {
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
        }
        finally
        {
            gate.Release();
        }
    }

    #endregion

    #region Internal helpers

    private NpgsqlConnectionManager.ConnectionEntry GetEntry(Transaction transaction)
    {
        return _connectionManager.GetEntry(transaction);
    }

    private SemaphoreSlim GetOrCreateTxLock(string txId)
    {
        return _connectionManager.GetOrCreateTxLock(txId);
    }

    internal void RemoveEntry(string txId)
    {
        _connectionManager.RemoveEntry(txId);
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

    #endregion

    #region IHealthCheckCapability

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _connectionManager.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connectionManager.Dispose();
    }

    #endregion
}
