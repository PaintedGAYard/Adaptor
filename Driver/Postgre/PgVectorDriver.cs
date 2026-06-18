using System.Collections.Concurrent;
using System.Transactions;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Adaptor.Driver.Postgre;

/// <summary>
/// pgvector Vector Driver — 提供向量 Upsert/Search 能力，基于 PostgreSQL
/// pgvector 扩展实现。与 <see cref="PostgreSqlDriver"/> 共享同库模式，
/// 通过 <see cref="ITransactionalResourceManager.Enlist"/> 参与分布式事务。
/// </summary>
/// <remarks>
/// 所需 PostgreSQL 扩展：<c>vector</c>（pgvector）。
/// 将在首次 Upsert 时自动创建表 <c>adaptor_vector_store</c>。
/// </remarks>
public sealed class PgVectorDriver :
    IResourceManager,
    ITransactionalResourceManager,
    IVectorUpsertCapability,
    IVectorSearchCapability,
    IHealthCheckCapability,
    IDisposable
{
    private const string DefaultTableName = "adaptor_vector_store";

    private readonly string _connectionString;
    private readonly ILogger<PgVectorDriver>? _logger;
    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();
    private bool _disposed;

    public string Name => "pgvector";
    public ResourceType ResourceType => ResourceType.Vector;
    public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();

    public PgVectorDriver(string connectionString, ILogger<PgVectorDriver>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _logger = logger;
    }

    // ─── ITransactionalResourceManager ──────────────────────────────────────

    public void Enlist(Transaction transaction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var txId = transaction.TransactionInformation.LocalIdentifier;

        if (_connections.ContainsKey(txId))
        {
            _logger?.LogDebug("PgVectorDriver already enlisted in transaction {TxId}", txId);
            return;
        }

        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        var localTransaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);

        var entry = new ConnectionEntry(connection, localTransaction, transaction);
        if (!_connections.TryAdd(txId, entry))
        {
            localTransaction.Dispose();
            connection.Dispose();
            return;
        }

        transaction.EnlistVolatile(new VectorEnlistmentHandler(this, txId), EnlistmentOptions.None);

        _logger?.LogDebug("PgVectorDriver enlisted in transaction {TxId}", txId);
    }

    // ─── IVectorUpsertCapability ────────────────────────────────────────────

    public async Task<VectorUpsertResult> UpsertAsync(VectorUpsertRequest request, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);
        await EnsureTableAsync(entry.Connection, entry.LocalTransaction, ct).ConfigureAwait(false);

        try
        {
            // Generate a deterministic ID if not provided
            var id = !string.IsNullOrEmpty(request.Id)
                ? request.Id
                : Guid.NewGuid().ToString("N");

            var vectorLiteral = VectorToLiteral(request.Vector);
            var metadataJson = MetadataToJson(request.Metadata);

            await using var cmd = entry.Connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {DefaultTableName} (collection, id, embedding, metadata)
                VALUES (@collection, @id, {vectorLiteral}::vector, {metadataJson}::jsonb)
                ON CONFLICT (collection, id) DO UPDATE
                SET embedding = {vectorLiteral}::vector,
                    metadata = {metadataJson}::jsonb
                """;
            cmd.Transaction = entry.LocalTransaction;
            cmd.Parameters.AddWithValue("collection", request.Collection);
            cmd.Parameters.AddWithValue("id", id);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _logger?.LogDebug("PgVectorDriver upserted vector id={Id} in collection={Collection}", id, request.Collection);

            return new VectorUpsertResult(id, true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PgVectorDriver UpsertAsync failed");
            return new VectorUpsertResult(request.Id ?? string.Empty, false, ex.Message);
        }
    }

    // ─── IVectorSearchCapability ────────────────────────────────────────────

    public async Task<VectorSearchResult> SearchAsync(VectorSearchRequest request, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = GetEntry(transaction);
        await EnsureTableAsync(entry.Connection, entry.LocalTransaction, ct).ConfigureAwait(false);

        try
        {
            var queryLiteral = VectorToLiteral(request.Vector);

            await using var cmd = entry.Connection.CreateCommand();
            // Use cosine distance (<=>) by default; configurable via metadata if needed
            cmd.CommandText = $"""
                SELECT id, metadata,
                       (embedding <=> {queryLiteral}::vector) AS distance
                FROM {DefaultTableName}
                WHERE collection = @collection
                ORDER BY embedding <=> {queryLiteral}::vector
                LIMIT @top_k
                """;
            cmd.Transaction = entry.LocalTransaction;
            cmd.Parameters.AddWithValue("collection", request.Collection);
            cmd.Parameters.AddWithValue("top_k", request.TopK);

            var start = DateTime.UtcNow;
            var hits = new List<VectorSearchHit>();

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                var metadata = reader.IsDBNull(1) ? null : DeserializeMetadata(reader.GetString(1));
                var distance = reader.GetDouble(2);

                // pgvector cosine distance is in [0, 2]; convert to similarity score [0, 1]
                var score = (float)(1.0 - distance / 2.0);

                hits.Add(new VectorSearchHit(id, score, metadata));
            }

            var duration = DateTime.UtcNow - start;

            _logger?.LogDebug("PgVectorDriver searched collection={Collection} returned {Count} hits in {Duration:F2}ms",
                request.Collection, hits.Count, duration.TotalMilliseconds);

            return new VectorSearchResult(hits, duration);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PgVectorDriver SearchAsync failed");
            return new VectorSearchResult(Array.Empty<VectorSearchHit>(), TimeSpan.Zero, ex.Message);
        }
    }

    // ─── IHealthCheckCapability ─────────────────────────────────────────────

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
            _logger?.LogWarning(ex, "PgVectorDriver health check failed");
            return false;
        }
    }

    // ─── Internal helpers ───────────────────────────────────────────────────

    private ConnectionEntry GetEntry(Transaction transaction)
    {
        var txId = transaction.TransactionInformation.LocalIdentifier;

        if (_connections.TryGetValue(txId, out var entry))
            return entry;

        throw new InvalidOperationException(
            $"Transaction (LocalIdentifier={txId}) is not enlisted with this driver. " +
            "Enlist() must be called before data operations.");
    }

    /// <summary>
    /// Auto-create the vector store table if it does not exist.
    /// The pgvector extension is expected to be installed; otherwise
    /// the <c>vector</c> type will not be recognised.
    /// </summary>
    private async Task EnsureTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {DefaultTableName} (
                collection TEXT NOT NULL,
                id TEXT NOT NULL,
                embedding vector,
                metadata JSONB,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (collection, id)
            )
            """;
        cmd.Transaction = transaction;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger?.LogDebug("PgVectorDriver ensured table {Table} exists", DefaultTableName);
    }

    /// <summary>
    /// Convert a float[] to a pgvector literal string: '[0.1,0.2,0.3]'
    /// </summary>
    private static string VectorToLiteral(float[] vector)
    {
        return $"'[{string.Join(",", vector.Select(v => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture)))}]'";
    }

    /// <summary>
    /// Serialize metadata dictionary to a JSON literal string for PostgreSQL.
    /// Returns 'NULL' if metadata is null.
    /// </summary>
    private static string MetadataToJson(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return "'{}'";

        var parts = metadata.Select(kvp =>
        {
            var key = System.Text.Json.JsonSerializer.Serialize(kvp.Key);
            var value = kvp.Value switch
            {
                null => "null",
                string s => System.Text.Json.JsonSerializer.Serialize(s),
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                double d => d.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
                float f => f.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                _ => System.Text.Json.JsonSerializer.Serialize(kvp.Value)
            };
            return $"{key}:{value}";
        });

        return $"{{{string.Join(",", parts)}}}";
    }

    /// <summary>
    /// Deserialize a JSON string to a dictionary.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? DeserializeMetadata(string json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
        if (dict == null) return null;

        return dict.ToDictionary<KeyValuePair<string, System.Text.Json.JsonElement>, string, object?>(
            kvp => kvp.Key,
            kvp => JsonElementToObject(kvp.Value));
    }

    private static object? JsonElementToObject(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// Called by <see cref="VectorEnlistmentHandler"/> when a transaction completes.
    /// </summary>
    internal void RemoveEntry(string txId)
    {
        if (_connections.TryRemove(txId, out var entry))
        {
            _logger?.LogDebug("PgVectorDriver removing transaction {TxId}", txId);
            entry.Dispose();
        }
    }

    // ─── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, entry) in _connections)
        {
            entry.Dispose();
        }
        _connections.Clear();
    }

    // ─── Nested types ───────────────────────────────────────────────────────

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

    private sealed class VectorEnlistmentHandler : IEnlistmentNotification
    {
        private readonly PgVectorDriver _driver;
        private readonly string _txId;

        public VectorEnlistmentHandler(PgVectorDriver driver, string txId)
        {
            _driver = driver;
            _txId = txId;
        }

        void IEnlistmentNotification.Prepare(PreparingEnlistment preparingEnlistment)
        {
            try
            {
                _driver._logger?.LogDebug("PgVectorDriver [{TxId}] Prepare: voting Prepared", _txId);
                preparingEnlistment.Prepared();
            }
            catch (Exception ex)
            {
                _driver._logger?.LogError(ex, "PgVectorDriver [{TxId}] Prepare: force rollback", _txId);
                preparingEnlistment.ForceRollback();
            }
        }

        void IEnlistmentNotification.Commit(Enlistment enlistment)
        {
            try
            {
                _driver._logger?.LogDebug("PgVectorDriver [{TxId}] Commit: committing local transaction", _txId);

                if (_driver._connections.TryGetValue(_txId, out var entry))
                {
                    entry.LocalTransaction.Commit();
                    _driver._logger?.LogDebug("PgVectorDriver [{TxId}] local transaction committed", _txId);
                }
            }
            catch (Exception ex)
            {
                _driver._logger?.LogError(ex, "PgVectorDriver [{TxId}] Commit failed", _txId);
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
                _driver._logger?.LogDebug("PgVectorDriver [{TxId}] Rollback: rolling back local transaction", _txId);

                if (_driver._connections.TryGetValue(_txId, out var entry))
                {
                    entry.LocalTransaction.Rollback();
                    _driver._logger?.LogDebug("PgVectorDriver [{TxId}] local transaction rolled back", _txId);
                }
            }
            catch (Exception ex)
            {
                _driver._logger?.LogError(ex, "PgVectorDriver [{TxId}] Rollback failed", _txId);
            }
            finally
            {
                _driver.RemoveEntry(_txId);
                enlistment.Done();
            }
        }

        void IEnlistmentNotification.InDoubt(Enlistment enlistment)
        {
            _driver._logger?.LogWarning("PgVectorDriver [{TxId}] InDoubt: transaction outcome unknown", _txId);
            _driver.RemoveEntry(_txId);
            enlistment.Done();
        }
    }
}
