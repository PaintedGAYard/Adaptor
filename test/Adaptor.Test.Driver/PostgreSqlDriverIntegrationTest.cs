using Npgsql;
using Testcontainers.PostgreSql;

namespace Adaptor.Test.Driver;

/// <summary>
/// Integration tests for <see cref="PostgreSqlDriver"/> using Testcontainers.
/// Requires Docker. Verifies actual SQL execution, query, parameter binding,
/// and transactional behavior against a real PostgreSQL database.
/// </summary>
public sealed class PostgreSqlDriverIntegrationTest : IAsyncLifetime
{
    private PostgreSqlContainer? _pgContainer;

    [Fact]
    public async Task ExecuteAsync_ShouldInsertAndReturnAffectedRows()
    {
        // Arrange
        await CreateTestTableAsync();
        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalExecuteRequest(
            "INSERT INTO adaptor_test_sql (id, label, value) VALUES (@id, @label, @value)",
            [
                new RelationalParameter("@id", 1),
                new RelationalParameter("@label", "test-A"),
                new RelationalParameter("@value", 42.5),
            ]);

        // Act
        var result = await driver.ExecuteAsync(request, tx);

        // Assert
        Assert.Null(result.ErrorMessage);
        Assert.Equal(1, result.AffectedRows);
        Assert.True(result.Duration > TimeSpan.Zero);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUpdateExistingRows()
    {
        // Arrange
        await CreateTestTableAsync();
        await InsertTestRowAsync(1, "original", 10.0);

        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalExecuteRequest(
            "UPDATE adaptor_test_sql SET label = @label WHERE id = @id",
            [
                new RelationalParameter("@id", 1),
                new RelationalParameter("@label", "updated"),
            ]);

        // Act
        var result = await driver.ExecuteAsync(request, tx);

        // Assert
        Assert.Null(result.ErrorMessage);
        Assert.Equal(1, result.AffectedRows);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task QueryAsync_ShouldReturnRows()
    {
        // Arrange
        await CreateTestTableAsync();
        await InsertTestRowAsync(1, "alpha", 1.0);
        await InsertTestRowAsync(2, "beta", 2.0);
        await InsertTestRowAsync(3, "gamma", 3.0);

        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalQueryRequest(
            "SELECT id, label, value FROM adaptor_test_sql ORDER BY id");

        // Act
        var result = await driver.QueryAsync(request, tx);

        // Assert
        Assert.Null(result.ErrorMessage);
        Assert.Equal(3, result.Rows.Count);

        Assert.Equal(1, result.Rows[0]["id"]);
        Assert.Equal("alpha", result.Rows[0]["label"]);
        Assert.Equal(2, result.Rows[1]["id"]);
        Assert.Equal("beta", result.Rows[1]["label"]);
        Assert.Equal(3, result.Rows[2]["id"]);
        Assert.Equal("gamma", result.Rows[2]["label"]);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task QueryAsync_ShouldHandleEmptyResultSet()
    {
        // Arrange
        await CreateTestTableAsync();

        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalQueryRequest(
            "SELECT id, label FROM adaptor_test_sql WHERE 1=0");

        // Act
        var result = await driver.QueryAsync(request, tx);

        // Assert
        Assert.Null(result.ErrorMessage);
        Assert.Empty(result.Rows);
        Assert.True(result.Duration > TimeSpan.Zero);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task QueryAsync_ShouldBindParameters()
    {
        // Arrange
        await CreateTestTableAsync();
        await InsertTestRowAsync(1, "target", 99.0);
        await InsertTestRowAsync(2, "other", 0.0);

        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalQueryRequest(
            "SELECT id, label FROM adaptor_test_sql WHERE label = @search",
            [new RelationalParameter("@search", "target")]);

        // Act
        var result = await driver.QueryAsync(request, tx);

        // Assert
        Assert.Null(result.ErrorMessage);
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["id"]);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReportErrorForInvalidSql()
    {
        // Arrange
        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalExecuteRequest("INVALID SQL STATEMENT");

        // Act
        var result = await driver.ExecuteAsync(request, tx);

        // Assert — should not throw, returns error message
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(0, result.AffectedRows);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task QueryAsync_ShouldReportErrorForInvalidSql()
    {
        // Arrange
        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalQueryRequest("SELECT * FROM nonexistent_table");

        // Act
        var result = await driver.QueryAsync(request, tx);

        // Assert — should not throw, returns error message
        Assert.NotNull(result.ErrorMessage);
        Assert.Empty(result.Rows);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task HealthCheck_ShouldReturnTrueForReachableDatabase()
    {
        // Arrange
        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        // Act
        var healthy = await driver.HealthCheckAsync();

        // Assert
        Assert.True(healthy);

        driver.Dispose();
    }

    [Fact]
    public async Task EnlistedTransaction_ShouldCommitAndPersistData()
    {
        // Arrange
        await CreateTestTableAsync();

        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalExecuteRequest(
            "INSERT INTO adaptor_test_sql (id, label, value) VALUES (100, 'committed', 1.0)");

        var result = await driver.ExecuteAsync(request, tx);
        Assert.Null(result.ErrorMessage);

        // Act — commit the transaction
        tx.Commit();

        // Assert — data should be visible to a new connection
        await using var verifyConn = new NpgsqlConnection(_pgContainer!.GetConnectionString());
        await verifyConn.OpenAsync();
        await using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = "SELECT label FROM adaptor_test_sql WHERE id = 100";
        var label = await verifyCmd.ExecuteScalarAsync();
        Assert.Equal("committed", label);

        // Cleanup
        await using var cleanupCmd = verifyConn.CreateCommand();
        cleanupCmd.CommandText = "DELETE FROM adaptor_test_sql WHERE id = 100";
        await cleanupCmd.ExecuteNonQueryAsync();

        driver.Dispose();
    }

    [Fact]
    public async Task EnlistedTransaction_ShouldRollbackAndNotPersistData()
    {
        // Arrange
        await CreateTestTableAsync();

        var driver = new PostgreSqlDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgreSqlDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new RelationalExecuteRequest(
            "INSERT INTO adaptor_test_sql (id, label, value) VALUES (200, 'rolled_back', 2.0)");

        var result = await driver.ExecuteAsync(request, tx);
        Assert.Null(result.ErrorMessage);

        // Act — rollback the transaction
        tx.Rollback();

        // Assert — data should NOT be visible to a new connection
        await using var verifyConn = new NpgsqlConnection(_pgContainer!.GetConnectionString());
        await verifyConn.OpenAsync();
        await using var verifyCmd = verifyConn.CreateCommand();
        verifyCmd.CommandText = "SELECT COUNT(*) FROM adaptor_test_sql WHERE id = 200";
        var count = (long)(await verifyCmd.ExecuteScalarAsync())!;
        Assert.Equal(0, count);

        driver.Dispose();
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
        if (_pgContainer != null)
        {
            await _pgContainer.DisposeAsync();
        }
    }

    #endregion

    #region Helpers

    private async Task CreateTestTableAsync()
    {
        await using var conn = new NpgsqlConnection(_pgContainer!.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS adaptor_test_sql (
                id INTEGER PRIMARY KEY,
                label TEXT,
                value DOUBLE PRECISION
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertTestRowAsync(int id, string label, double value)
    {
        await using var conn = new NpgsqlConnection(_pgContainer!.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO adaptor_test_sql (id, label, value) VALUES (@id, @label, @value)";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("label", label);
        cmd.Parameters.AddWithValue("value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion
}
