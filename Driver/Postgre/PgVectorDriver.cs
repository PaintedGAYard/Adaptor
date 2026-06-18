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
    IRelationalVectorSearchCapability,
    IHealthCheckCapability,
    IDisposable
{
    private const string DefaultTableName = "adaptor_vector_store";
    private const string DimensionTableName = "adaptor_vector_dimensions";

    private readonly string _connectionString;
    private readonly ILogger<PgVectorDriver>? _logger;
    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _txLocks = new();
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

    // ─── IRelationalVectorSearchCapability ─────────────────────────────────

    public async Task<VectorSearchResult> SearchAsync(RelationalVectorSearchRequest request, Transaction transaction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        if (request.DenseVector == null && request.SparseVector == null)
            throw new ArgumentException("Either DenseVector or SparseVector must be provided.", nameof(request));

        var txId = transaction.TransactionInformation.LocalIdentifier;
        var gate = GetOrCreateTxLock(txId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = GetEntry(transaction);
            await EnsureTableAsync(entry.Connection, entry.LocalTransaction, ct).ConfigureAwait(false);

            await using var cmd = entry.Connection.CreateCommand();
            cmd.Transaction = entry.LocalTransaction;

            string vectorStr;
            string columnName;

            if (request.DenseVector != null)
            {
                vectorStr = DenseVectorToString(request.DenseVector);
                columnName = request.VectorColumn;
            }
            else
            {
                vectorStr = SparseVectorToString(request.SparseVector!);
                columnName = request.VectorColumn;
            }

            var castType = request.DenseVector != null ? "vector" : "sparsevec";

            // Build WHERE: append user-provided filter clause if present
            var whereClause = string.IsNullOrWhiteSpace(request.WhereClause)
                ? ""
                : $" AND ({request.WhereClause})";

            cmd.CommandText = $"""
                SELECT id, metadata,
                       ({columnName} <=> @vector::{castType}) AS distance
                FROM {request.Table}
                WHERE 1=1{whereClause}
                ORDER BY {columnName} <=> @vector::{castType}
                LIMIT @top_k
                """;
            cmd.Parameters.AddWithValue("vector", vectorStr);
            cmd.Parameters.AddWithValue("top_k", request.TopK);

            if (request.Parameters != null)
            {
                foreach (var p in request.Parameters)
                {
                    cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
                }
            }

            var start = DateTime.UtcNow;
            var hits = new List<VectorSearchHit>();

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                var metadata = reader.IsDBNull(1) ? null : DeserializeMetadata(reader.GetString(1));
                var distance = reader.GetDouble(2);

                var score = (float)(1.0 - distance / 2.0);

                hits.Add(new VectorSearchHit(id, score, metadata));
            }

            var duration = DateTime.UtcNow - start;

            _logger?.LogDebug("PgVectorDriver searched table={Table} returned {Count} hits in {Duration:F2}ms",
                request.Table, hits.Count, duration.TotalMilliseconds);

            return new VectorSearchResult(hits, duration);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PgVectorDriver SearchAsync failed");
            return new VectorSearchResult(Array.Empty<VectorSearchHit>(), TimeSpan.Zero, ex.Message);
        }
        finally
        {
            gate.Release();
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
    /// Auto-create the vector store and dimension tracking tables if they do not exist.
    /// The pgvector extension is expected to be installed; otherwise
    /// the <c>vector</c>/<c>sparsevec</c> types will not be recognised.
    /// </summary>
    private async Task EnsureTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;

        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {DefaultTableName} (
                collection TEXT NOT NULL,
                id TEXT NOT NULL,
                embedding vector,
                sparse_embedding sparsevec,
                metadata JSONB,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (collection, id)
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Dimension tracking table — records the expected vector dimension per collection
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {DimensionTableName} (
                collection TEXT PRIMARY KEY,
                dimension INT NOT NULL CHECK (dimension > 0),
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger?.LogDebug("PgVectorDriver ensured tables {Table} and {DimTable} exist",
            DefaultTableName, DimensionTableName);
    }

    /// <summary>
    /// Validate that the input vector dimension matches the collection's expected dimension.
    /// If no dimension is recorded yet, record it.
    /// </summary>
    private async Task ValidateDimension(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string collection, int inputDim, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;

        // Try to read existing dimension
        cmd.CommandText = $"SELECT dimension FROM {DimensionTableName} WHERE collection = @collection";
        cmd.Parameters.AddWithValue("collection", collection);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (result == null)
        {
            // First upsert for this collection — record dimension
            cmd.Parameters.Clear();
            cmd.CommandText = $"""
                INSERT INTO {DimensionTableName} (collection, dimension)
                VALUES (@collection, @dim)
                ON CONFLICT (collection) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("collection", collection);
            cmd.Parameters.AddWithValue("dim", inputDim);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var expected = Convert.ToInt32(result);
            if (inputDim != expected)
            {
                throw new InvalidOperationException(
                    $"Vector dimension mismatch for collection '{collection}': " +
                    $"expected {expected}, got {inputDim}.");
            }
        }
    }

    /// <summary>
    /// Convert a float[] to a pgvector-compatible string: '[0.1,0.2,0.3]'
    /// Used as a parameter value with ::vector cast.
    /// </summary>
    private static string DenseVectorToString(float[] vector)
    {
        return $"[{string.Join(",", vector.Select(v => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture)))}]";
    }

    /// <summary>
    /// Convert a SparseVector to a pgvector sparsevec-compatible string: '{idx1:val1,idx2:val2}'
    /// Used as a parameter value with ::sparsevec cast.
    /// </summary>
    private static string SparseVectorToString(SparseVector vector)
    {
        if (vector.Indices.Length != vector.Values.Length)
            throw new ArgumentException("Indices and Values must have the same length.");

        var parts = vector.Indices.Zip(vector.Values, (idx, val) =>
            $"{idx}:{val.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}");
        return $"{{{string.Join(",", parts)}}}";
    }

    /// <summary>
    /// Serialize metadata dictionary to a JSON string.
    /// Used as a parameter value with ::jsonb cast.
    /// </summary>
    private static string MetadataToJsonString(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return "{}";

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
    /// Deserialize a pgvector vector literal '[0.1,0.2,0.3]' to a float[].
    /// </summary>
    private static float[]? DeserializeDenseVector(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        // Format: [0.1,0.2,0.3]
        var trimmed = value.Trim('[', ']');
        if (trimmed.Length == 0) return Array.Empty<float>();
        return trimmed.Split(',')
            .Select(s => float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f)
            .ToArray();
    }

    /// <summary>
    /// Deserialize a pgvector sparsevec literal '{idx1:val1,idx2:val2}' to a SparseVector.
    /// </summary>
    private static SparseVector? DeserializeSparseVector(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        // Format: {idx1:val1,idx2:val2}
        var trimmed = value.Trim('{', '}');
        if (trimmed.Length == 0) return new SparseVector([], []);
        var pairs = trimmed.Split(',');
        var indices = new int[pairs.Length];
        var values = new float[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            var parts = pairs[i].Split(':');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out indices[i]);
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out values[i]);
            }
        }
        return new SparseVector(indices, values);
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

        if (_txLocks.TryRemove(txId, out var gate))
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// Get or create a per-transaction concurrency lock.
    /// </summary>
    private SemaphoreSlim GetOrCreateTxLock(string txId)
    {
        return _txLocks.GetOrAdd(txId, _ => new SemaphoreSlim(1, 1));
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

        foreach (var (_, gate) in _txLocks)
        {
            gate.Dispose();
        }
        _txLocks.Clear();
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
