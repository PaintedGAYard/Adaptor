using System.Transactions;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;

namespace Adaptor.Driver.Postgre;

/// <summary>
/// pgvector Vector Driver providing upsert/search capabilities via the
/// PostgreSQL pgvector extension. Shares the same database schema with
/// <see cref="PostgreSqlDriver"/> and participates in distributed
/// transactions via <see cref="ITransactionalResourceManager.Enlist"/>.
/// </summary>
/// <remarks>
/// Requires the PostgreSQL <c>vector</c> extension (pgvector).
/// The <c>adaptor_vector_store</c> table is auto-created on first search.
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

    private readonly string? _connectionString;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly NpgsqlConnectionManager _connectionManager;
    private readonly ILogger<PgVectorDriver>? _logger;
    private bool _disposed;

    public string Name => "pgvector";
    public ResourceType ResourceType => ResourceType.Vector;
    public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();

    internal NpgsqlConnectionManager ConnectionManager => _connectionManager;

    /// <summary>Create with a raw connection string (no pgvector type mappings).</summary>
    public PgVectorDriver(string connectionString, ILogger<PgVectorDriver>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _connectionManager = new NpgsqlConnectionManager(connectionString, Name, logger);
        _logger = logger;
    }

    /// <summary>Create with an NpgsqlDataSource (recommended; supports UseVector()).</summary>
    public PgVectorDriver(NpgsqlDataSource dataSource, ILogger<PgVectorDriver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
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

    #region IRelationalVectorSearchCapability

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
            // Contract validation — fails fast (InvalidOperationException) if not enlisted
            var entry = GetEntry(transaction);

            await EnsureTableAsync(entry.Connection, entry.LocalTransaction, ct).ConfigureAwait(false);

            await using var cmd = entry.Connection.CreateCommand();
            cmd.Transaction = entry.LocalTransaction;

            // Build vector parameter using pgvector-dotnet types when DataSource is available.
            // Fall back to string formatting for raw connection strings.
            var vectorParam = CreateVectorParameter(request);
            var columnName = request.VectorColumn;
            var castSuffix = _dataSource != null ? "" : (request.DenseVector != null ? "::vector" : "::sparsevec");

            // Quote identifiers to prevent SQL injection via table/column names
            var safeTable = QuotePgIdentifier(request.Table);
            var safeColumn = QuotePgIdentifier(columnName);

            // Build WHERE: append user-provided filter clause if present.
            // WhereClause must use @param references; never concatenate literal values.
            var whereClause = string.IsNullOrWhiteSpace(request.WhereClause)
                ? ""
                : $" AND ({request.WhereClause})";

            cmd.CommandText = $"""
                SELECT id, metadata,
                       ({safeColumn} <=> @vector{castSuffix}) AS distance
                FROM {safeTable}
                WHERE 1=1{whereClause}
                ORDER BY {safeColumn} <=> @vector{castSuffix}
                LIMIT @top_k
                """;
            cmd.Parameters.AddWithValue("vector", vectorParam);
            cmd.Parameters.AddWithValue("top_k", request.TopK);

            if (request.Parameters != null)
            {
                foreach (var p in request.Parameters)
                {
                    cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
                }
            }

            var start = DateTime.UtcNow;
            try
            {
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _connectionManager.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    #endregion

    #region Internal helpers

    private NpgsqlConnectionManager.ConnectionEntry GetEntry(Transaction transaction)
    {
        return _connectionManager.GetEntry(transaction);
    }

    /// <summary>Auto-create the vector store and dimension tracking tables if they do not exist.</summary>
    /// <remarks>Requires the pgvector extension; otherwise <c>vector</c>/<c>sparsevec</c> types will not be recognised.
    /// The <c>vector</c> extension is created first via a separate non-transactional connection
    /// because <c>CREATE EXTENSION</c> cannot run inside a transaction block.</remarks>
    private async Task EnsureTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await EnsureExtensionAsync(ct).ConfigureAwait(false);

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
    /// Ensure the pgvector extension is installed.
    /// Uses a separate non-transactional connection because PostgreSQL
    /// does not allow <c>CREATE EXTENSION</c> inside a transaction block.
    /// </summary>
    private async Task EnsureExtensionAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger?.LogDebug("PgVectorDriver ensured pgvector extension exists");
    }

    /// <summary>Validate or record the expected vector dimension for a collection.</summary>
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
    /// Create a pgvector-dotnet <see cref="Vector"/> or <see cref="SparseVector"/>
    /// from the search request. Uses CLR types when a DataSource is available
    /// (for proper Npgsql type mapping), otherwise falls back to a string that
    /// relies on the SQL ::vector / ::sparsevec cast.
    /// </summary>
    private object CreateVectorParameter(RelationalVectorSearchRequest request)
    {
        if (request.DenseVector != null)
        {
            return _dataSource != null
                ? new Vector(request.DenseVector)
                : (object)$"[{string.Join(",", request.DenseVector.Select(v => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture)))}]";
        }

        if (request.SparseVector != null)
        {
            var sv = request.SparseVector;
            if (sv.Indices.Length != sv.Values.Length)
                throw new ArgumentException("Indices and Values must have the same length.");

            return _dataSource != null
                ? new Pgvector.SparseVector(request.SparseVector.Values.Length, sv.Indices, sv.Values)
                : (object)BuildSparseVecString(sv);
        }

        throw new ArgumentException("Either DenseVector or SparseVector must be provided.");
    }

    /// <summary>Build a sparsevec string literal for the fallback path.</summary>
    private static string BuildSparseVecString(Coordinator.Models.SparseVector vector)
    {
        var parts = vector.Indices.Zip(vector.Values, (idx, val) =>
            $"{idx}:{val.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}");
        return $"{{{string.Join(",", parts)}}}";
    }

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
    /// Quote a PostgreSQL identifier with double quotes, preventing SQL injection.
    /// </summary>
    /// <remarks>Escapes embedded double quotes by doubling them.</remarks>
    private static string QuotePgIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    internal void RemoveEntry(string txId)
    {
        _connectionManager.RemoveEntry(txId);
    }

    private SemaphoreSlim GetOrCreateTxLock(string txId)
    {
        return _connectionManager.GetOrCreateTxLock(txId);
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
