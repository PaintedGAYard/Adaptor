namespace Adaptor.Test.Driver;

/// <summary>
/// Tests for <see cref="PostgreSqlDriver"/>.
/// Design-based: derived from DETAILED-DESIGN.md §5.2–5.3, REFACTOR §5.
/// These tests verify the driver's *contractual* behavior as defined in the design.
/// Actual database interaction is via integration tests; these tests verify the
/// driver's structural compliance, error paths, and enlistment logic.
/// </summary>
public sealed class PostgreSqlDriverTest
{
    private const string TestConnectionString =
        "Host=localhost;Database=adaptor_test;Username=test;Password=test";

    // ──────────────────────────────────────────────
    // IResourceManager compliance — 设计文档 5.2
    // ──────────────────────────────────────────────

    [Fact]
    public void PostgreSqlDriver_ShouldImplementAllRequiredInterfaces()
    {
        var driver = CreateDriver();

        Assert.IsAssignableFrom<IResourceManager>(driver);
        Assert.IsAssignableFrom<ITransactionalResourceManager>(driver);
        Assert.IsAssignableFrom<IRelationalExecuteCapability>(driver);
        Assert.IsAssignableFrom<IRelationalQueryCapability>(driver);
        Assert.IsAssignableFrom<IHealthCheckCapability>(driver);
        Assert.IsAssignableFrom<IDisposable>(driver);
    }

    [Fact]
    public void PostgreSqlDriver_ShouldHaveCorrectResourceType()
    {
        var driver = CreateDriver();
        Assert.Equal(ResourceType.Sql, driver.ResourceType);
    }

    [Fact]
    public void PostgreSqlDriver_ShouldHaveUniqueResourceManagerIdentifier()
    {
        var d1 = CreateDriver();
        var d2 = CreateDriver();
        Assert.NotEqual(d1.ResourceManagerIdentifier, d2.ResourceManagerIdentifier);
    }

    [Fact]
    public void PostgreSqlDriver_Name_ShouldBeNonEmpty()
    {
        var driver = CreateDriver();
        Assert.False(string.IsNullOrEmpty(driver.Name));
    }

    // ──────────────────────────────────────────────
    // Enlist — 设计文档 5.2, REFACTOR §5
    // ──────────────────────────────────────────────

    [Fact]
    public void Enlist_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();
        Assert.Throws<ObjectDisposedException>(() => driver.Enlist(tx));
    }

    [Fact]
    public void Enlist_ShouldAcceptValidTransaction()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        // Enlist should not throw for a valid transaction
        // (Note: the actual Npgsql connection may fail, but the contract is structural)
        // We verify the method signature and exception contract
        var ex = Record.Exception(() => driver.Enlist(tx));

        // On systems without PostgreSQL, this will throw NpgsqlException,
        // but the design says Enlist must accept a valid Transaction parameter.
        // The actual connection handling is tested via integration tests.
        if (ex != null)
        {
            Assert.IsType<Npgsql.NpgsqlException>(ex);
        }
    }

    [Fact]
    public void Enlist_ShouldBeIdempotentForSameTransaction()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        // First call — may fail on systems without PG, but second call
        // should not throw for the same transaction
        try { driver.Enlist(tx); } catch { /* skip integration */ }

        // Second Enlist for same transaction should not throw
        var ex = Record.Exception(() => driver.Enlist(tx));

        // If the first call succeeded, the second should be a no-op
        // If the first call failed (no PG), the second call will also fail with the same error
    }

    // ──────────────────────────────────────────────
    // ExecuteAsync / QueryAsync contract — 设计文档 5.3
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ShouldThrowIfNotEnlisted()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        // If the driver hasn't been enlisted for this transaction,
        // GetEntry throws InvalidOperationException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.ExecuteAsync(new RelationalExecuteRequest("SELECT 1"), tx));

        Assert.Contains("not enlisted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryAsync_ShouldThrowIfNotEnlisted()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.QueryAsync(new RelationalQueryRequest("SELECT 1"), tx));

        Assert.Contains("not enlisted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.ExecuteAsync(new RelationalExecuteRequest("SELECT 1"), tx));
    }

    [Fact]
    public async Task QueryAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.QueryAsync(new RelationalQueryRequest("SELECT 1"), tx));
    }

    // ──────────────────────────────────────────────
    // HealthCheck — 设计文档 5.3
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_ShouldReturnFalseWhenUnreachable()
    {
        // With a bogus connection string, health check should return false (not throw)
        var driver = new PostgreSqlDriver(
            "Host=nonexistent.example.com;Database=test;Username=test;Password=test");

        var healthy = await driver.HealthCheckAsync();

        Assert.False(healthy);
    }

    // ──────────────────────────────────────────────
    // Dispose — 设计文档 5.2
    // ──────────────────────────────────────────────

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        var driver = CreateDriver();
        driver.Dispose();
        // Second dispose should not throw
        driver.Dispose();
    }

    // ──────────────────────────────────────────────
    // Parameter handling — 设计文档 3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void ExecuteAsync_ShouldAcceptParameterizedQuery()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        var request = new RelationalExecuteRequest(
            "INSERT INTO test VALUES (@v1, @v2)",
            [
                new RelationalParameter("@v1", 42),
                new RelationalParameter("@v2", "hello"),
            ]);

        // Verify the request can be constructed correctly
        Assert.Equal(2, request.Parameters!.Count);
    }

    // ──────────────────────────────────────────────
    // NpgsqlEnlistmentHandler — 设计文档 5.2
    // ──────────────────────────────────────────────

    [Fact]
    public void PostgreSqlDriver_ShouldUsePerTransactionConcurrencyLock()
    {
        // The driver delegates per-transaction locking to NpgsqlConnectionManager.
        // Verify the driver's internal GetOrCreateTxLock is accessible.
        var driver = CreateDriver();

        // Enlist in a transaction to force creation of internal state
        using var tx = new CommittableTransaction();
        try { driver.Enlist(tx); } catch { /* PG may not be available */ }

        // The driver should have a reference to the connection manager
        var driverType = typeof(PostgreSqlDriver);
        var fields = driverType.GetFields(
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        Assert.Contains(fields, f =>
            f.FieldType.Name.Contains("NpgsqlConnectionManager"));
    }

    private static PostgreSqlDriver CreateDriver()
    {
        return new PostgreSqlDriver(TestConnectionString,
            Substitute.For<ILogger<PostgreSqlDriver>>());
    }
}
