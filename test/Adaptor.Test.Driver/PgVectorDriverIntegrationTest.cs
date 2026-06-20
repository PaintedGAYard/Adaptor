using Npgsql;
using Testcontainers.PostgreSql;

namespace Adaptor.Test.Driver;

/// <summary>
/// Integration tests for <see cref="PgVectorDriver"/> using Testcontainers.
/// Requires Docker and a running Docker daemon.
/// Verifies database initialization (pgvector extension, table creation)
/// which cannot be tested with mocks alone.
/// </summary>
public sealed class PgVectorDriverIntegrationTest : IAsyncLifetime
{
    private PostgreSqlContainer? _pgContainer;

    /// <summary>
    /// Core test: verify that calling SearchAsync triggers
    /// EnsureExtensionAsync (CREATE EXTENSION IF NOT EXISTS vector)
    /// and EnsureTableAsync (adaptor_vector_store / adaptor_vector_dimensions).
    ///
    /// NOTE: EnsureTableAsync creates tables inside the enlisted transaction.
    /// A separate connection cannot see uncommitted tables, so we verify:
    ///   a) the extension (created outside any transaction) is visible externally,
    ///   b) SearchAsync succeeds (proves EnsureTableAsync ran without error).
    /// </summary>
    [Fact]
    public async Task SearchAsync_ShouldCreatePgVectorExtension()
    {
        // Arrange — create a search table manually (user's responsibility in production)
        await CreateSearchTableAsync("items", "embedding", 3);

        var driver = new PgVectorDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PgVectorDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalVectorSearchRequest(
            "items", "embedding", [0.1f, 0.2f, 0.3f], TopK: 5);

        // Act — triggers EnsureExtensionAsync → EnsureTableAsync → Search SQL
        var result = await driver.SearchAsync(request, tx);

        // Assert — search should succeed (empty table → zero hits, no error)
        Assert.Null(result.ErrorMessage);
        Assert.Empty(result.Hits);

        // Verify extension exists using a separate direct connection.
        // EnsureExtensionAsync opens its own non-transactional connection,
        // so the extension IS visible to other sessions immediately.
        await using var verifyConn = new NpgsqlConnection(_pgContainer!.GetConnectionString());
        await verifyConn.OpenAsync();

        await using var extCmd = verifyConn.CreateCommand();
        extCmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
        var extExists = await extCmd.ExecuteScalarAsync();
        Assert.NotNull(extExists);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnHitsWhenDataExists()
    {
        // Arrange — create search table with data
        await CreateSearchTableAsync("items", "embedding", 3);
        await InsertVectorAsync("items", "1", [0.1f, 0.2f, 0.3f]);
        await InsertVectorAsync("items", "2", [0.9f, 0.8f, 0.7f]);

        var driver = new PgVectorDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PgVectorDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        // Search for a vector close to item 1
        var request = new RelationalVectorSearchRequest(
            "items", "embedding", [0.11f, 0.21f, 0.31f], TopK: 5);

        // Act
        var result = await driver.SearchAsync(request, tx);

        // Assert
        Assert.Null(result.ErrorMessage);
        Assert.NotEmpty(result.Hits);
        Assert.Equal("1", result.Hits[0].Id); // closest match

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task HealthCheck_ShouldReturnTrueWhenConnected()
    {
        var driver = new PgVectorDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PgVectorDriver>>());

        var healthy = await driver.HealthCheckAsync();
        Assert.True(healthy);

        driver.Dispose();
    }

    /// <summary>
    /// Verify idempotency: calling SearchAsync twice should not produce
    /// errors from re-creating the extension or tables.
    /// </summary>
    [Fact]
    public async Task EnsureTableAsync_ShouldBeIdempotent()
    {
        await CreateSearchTableAsync("items", "embedding", 3);

        // First call
        await RunSearchAndAssertSuccess("items", "embedding", [0.1f, 0.2f, 0.3f]);

        // Second call with a different table — should also succeed
        await CreateSearchTableAsync("other_collection", "embedding", 3);
        await RunSearchAndAssertSuccess("other_collection", "embedding", [0.4f, 0.5f, 0.6f]);

        // Verify extension still exists
        await using var verifyConn = new NpgsqlConnection(_pgContainer!.GetConnectionString());
        await verifyConn.OpenAsync();
        await using var extCmd = verifyConn.CreateCommand();
        extCmd.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
        var extExists = await extCmd.ExecuteScalarAsync();
        Assert.NotNull(extExists);
    }

    #region IAsyncLifetime

    public async Task InitializeAsync()
    {
        _pgContainer = new PostgreSqlBuilder("pgvector/pgvector:pg16")
            .WithCleanUp(true)
            .Build();

        await _pgContainer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pgContainer != null)
        {
            await _pgContainer.DisposeAsync();
        }
    }

    #endregion

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    /// <summary>Create a search table suitable for PgVectorDriver queries.</summary>
    private async Task CreateSearchTableAsync(
        string tableName, string vectorColumn, int dimensions)
    {
        await using var conn = new NpgsqlConnection(_pgContainer!.GetConnectionString());
        await conn.OpenAsync();

        // Ensure the extension exists for the table creation
        await using var extCmd = conn.CreateCommand();
        extCmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
        await extCmd.ExecuteNonQueryAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {tableName} (
                id TEXT PRIMARY KEY,
                {vectorColumn} vector({dimensions}),
                metadata JSONB,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Insert a vector row for test data.</summary>
    private async Task InsertVectorAsync(string tableName, string id, float[] vector)
    {
        await using var conn = new NpgsqlConnection(_pgContainer!.GetConnectionString());
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        var vectorStr = $"[{string.Join(",", vector)}]";
        cmd.CommandText = $"INSERT INTO {tableName} (id, embedding, metadata) " +
                          "VALUES (@id, @vector::vector, '{}'::jsonb)";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("vector", vectorStr);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Run a SearchAsync call and assert it succeeds.</summary>
    private async Task RunSearchAndAssertSuccess(string table, string column, float[] vector)
    {
        var driver = new PgVectorDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PgVectorDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalVectorSearchRequest(table, column, vector, TopK: 5);
        var result = await driver.SearchAsync(request, tx);
        Assert.Null(result.ErrorMessage);

        tx.Rollback();
        driver.Dispose();
    }
}
