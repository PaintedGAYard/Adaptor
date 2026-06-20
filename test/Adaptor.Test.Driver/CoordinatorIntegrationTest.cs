using Npgsql;
using Testcontainers.PostgreSql;

namespace Adaptor.Test.Driver;

/// <summary>
/// Integration tests connecting <see cref="TransactionCoordinator"/>
/// with real PostgreSQL drivers via Testcontainers.
/// Verifies multi-driver 2PC across SQL + Vector + BLOB capabilities.
/// </summary>
public sealed class CoordinatorIntegrationTest : IAsyncLifetime
{
    private PostgreSqlContainer? _pgContainer;
    private TransactionCoordinator? _coordinator;

    [Fact]
    public async Task Coordinator_ShouldCommitSqlExecutesAcrossDrivers()
    {
        // Arrange — create search table for vector queries + test table for SQL
        await CreateSearchTableAsync("items", "embedding", 3);
        await CreateSearchTableAsync("sql_test", "ignored_vector", 1, "value INTEGER");

        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        var vectorDriver = new PgVectorDriver(_pgContainer.GetConnectionString(),
            Substitute.For<ILogger<PgVectorDriver>>());

        var blobDriver = new PostgresBlobDriver(_pgContainer.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        _coordinator = new TransactionCoordinator(
            [driver, vectorDriver, blobDriver],
            new SessionManager(
                Options.Create(new CoordinatorOptions
                {
                    DefaultTransactionTimeout = TimeSpan.FromMinutes(5),
                    MaxTransactionTimeout = TimeSpan.FromHours(1),
                }),
                Substitute.For<ILogger<SessionManager>>()),
            Options.Create(new CoordinatorOptions
            {
                DefaultTransactionTimeout = TimeSpan.FromMinutes(5),
                MaxTransactionTimeout = TimeSpan.FromHours(1),
            }),
            Substitute.For<ILogger<TransactionCoordinator>>());

        // Act — begin transaction
        var beginResult = await _coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        // 1. Execute SQL INSERT via coordinator
        var insertResult = await _coordinator.ExecuteOnCapabilityAsync<IRelationalExecuteCapability, RelationalExecuteResult>(
            txId,
            async (cap, tx) =>
            {
                var req = new RelationalExecuteRequest("INSERT INTO sql_test (id, name, value) VALUES ('1', 'test', 42)");
                return await cap.ExecuteAsync(req, tx);
            });
        Assert.Null(insertResult.ErrorMessage);
        Assert.Equal(1, insertResult.AffectedRows);

        // 2. Vector search (table exists but empty → 0 results, no error)
        var searchResult = await _coordinator.ExecuteOnCapabilityAsync<IRelationalVectorSearchCapability, VectorSearchResult>(
            txId,
            async (cap, tx) =>
            {
                var req = new RelationalVectorSearchRequest("items", "embedding", [0.1, 0.2, 0.3], TopK: 5);
                return await cap.SearchAsync(req, tx);
            });
        Assert.Null(searchResult.ErrorMessage);
        Assert.Empty(searchResult.Hits);

        // 3. BLOB upload + download
        var blobData = "Integration test blob"u8.ToArray();
        var uploadResult = await _coordinator.ExecuteOnCapabilityAsync<IBlobUploadCapability, BlobUploadResult>(
            txId,
            async (cap, tx) =>
            {
                var req = new BlobUploadRequest("int-key", blobData);
                return await cap.UploadAsync(req, tx);
            });
        Assert.Null(uploadResult.ErrorMessage);
        Assert.Equal(blobData.LongLength, uploadResult.Size);

        var downloadResult = await _coordinator.ExecuteOnCapabilityAsync<IBlobDownloadCapability, BlobDownloadResult>(
            txId,
            async (cap, tx) =>
            {
                var req = new BlobDownloadRequest("int-key");
                return await cap.DownloadAsync(req, tx);
            });
        Assert.Null(downloadResult.ErrorMessage);
        Assert.Equal(blobData, downloadResult.Data);

        // 4. Commit — all operations should succeed together
        var commitResult = await _coordinator.CommitTransactionAsync(txId);
        Assert.Equal(CommitStatus.Committed, commitResult.Status);

        // 5. Verify data persisted after commit
        await using var verifyConn = new NpgsqlConnection(_pgContainer.GetConnectionString());
        await verifyConn.OpenAsync();

        await using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = "SELECT id, name, value FROM sql_test WHERE id = '1'";
        await using var reader = await verifyCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("1", reader.GetString(0));
        Assert.Equal("test", reader.GetString(1));
        Assert.Equal(42, reader.GetInt32(2));

        _coordinator.Dispose();
    }

    [Fact]
    public async Task Coordinator_ShouldRollbackWithoutPersistingData()
    {
        await CreateSearchTableAsync("sql_rollback_test", "ignored_vector", 1, "value INTEGER");

        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        _coordinator = CreateCoordinatorWithDriver(driver);

        var beginResult = await _coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        // Insert data
        await _coordinator.ExecuteOnCapabilityAsync<IRelationalExecuteCapability, RelationalExecuteResult>(
            txId,
            async (cap, tx) =>
            {
                var req = new RelationalExecuteRequest("INSERT INTO sql_rollback_test (id, name, value) VALUES ('99', 'rollback-me', 999)");
                return await cap.ExecuteAsync(req, tx);
            });

        // Rollback
        await _coordinator.RollbackTransactionAsync(txId);

        // Verify data NOT persisted
        await using var verifyConn = new NpgsqlConnection(_pgContainer.GetConnectionString());
        await verifyConn.OpenAsync();
        await using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = "SELECT COUNT(*) FROM sql_rollback_test WHERE id = '99'";
        var count = (long)(await verifyCmd.ExecuteScalarAsync())!;
        Assert.Equal(0, count);
    }

    #region IAsyncLifetime

    public async Task InitializeAsync()
    {
        _pgContainer = new PostgreSqlBuilder("pgvector/pgvector:0.8.0-pg17")
            .WithCleanUp(true)
            .Build();
        await _pgContainer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _coordinator?.Dispose();
        if (_pgContainer != null) await _pgContainer.DisposeAsync();
    }

    #endregion

    #region Helpers

    private static TransactionCoordinator CreateCoordinatorWithDriver(IResourceManager driver)
    {
        var options = Options.Create(new CoordinatorOptions
        {
            DefaultTransactionTimeout = TimeSpan.FromMinutes(5),
            MaxTransactionTimeout = TimeSpan.FromHours(1),
        });
        return new TransactionCoordinator(
            [driver],
            new SessionManager(options, Substitute.For<ILogger<SessionManager>>()),
            options,
            Substitute.For<ILogger<TransactionCoordinator>>());
    }

    private async Task CreateSearchTableAsync(
        string tableName, string vectorColumn, int dimensions,
        string? extraColumns = null)
    {
        await using var conn = new NpgsqlConnection(_pgContainer!.GetConnectionString());
        await conn.OpenAsync();
        await using var extCmd = conn.CreateCommand();
        extCmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
        await extCmd.ExecuteNonQueryAsync();

        await using var cmd = conn.CreateCommand();
        var cols = $"id TEXT PRIMARY KEY, name TEXT, metadata JSONB DEFAULT '{{}}', {vectorColumn} vector({dimensions})";
        if (extraColumns != null) cols = $"id TEXT PRIMARY KEY, name TEXT, metadata JSONB DEFAULT '{{}}', {extraColumns}";
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {tableName} ({cols})";
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion
}
