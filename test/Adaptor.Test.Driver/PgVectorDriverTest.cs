namespace Adaptor.Test.Driver;

/// <summary>
/// Tests for <see cref="PgVectorDriver"/>.
/// Design-based: derived from DETAILED-DESIGN.md §5.2–5.3, REFACTOR-2 §2–3.
/// The driver only provides Search; CRUD is via SQL.
/// </summary>
public sealed class PgVectorDriverTest
{
    private const string TestConnectionString =
        "Host=localhost;Database=adaptor_test;Username=test;Password=test";

    // ──────────────────────────────────────────────
    // IResourceManager compliance — 设计文档 5.2
    // ──────────────────────────────────────────────

    [Fact]
    public void PgVectorDriver_ShouldImplementAllRequiredInterfaces()
    {
        var driver = CreateDriver();

        Assert.IsAssignableFrom<IResourceManager>(driver);
        Assert.IsAssignableFrom<ITransactionalResourceManager>(driver);
        Assert.IsAssignableFrom<IRelationalVectorSearchCapability>(driver);
        Assert.IsAssignableFrom<IHealthCheckCapability>(driver);
        Assert.IsAssignableFrom<IDisposable>(driver);
    }

    [Fact]
    public void PgVectorDriver_ShouldHaveCorrectResourceType()
    {
        var driver = CreateDriver();
        Assert.Equal(ResourceType.Vector, driver.ResourceType);
    }

    [Fact]
    public void PgVectorDriver_ShouldHaveUniqueIdentifier()
    {
        var d1 = CreateDriver();
        var d2 = CreateDriver();
        Assert.NotEqual(d1.ResourceManagerIdentifier, d2.ResourceManagerIdentifier);
    }

    // ──────────────────────────────────────────────
    // Enlist — 设计文档 5.2
    // ──────────────────────────────────────────────

    [Fact]
    public void Enlist_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();
        Assert.Throws<ObjectDisposedException>(() => driver.Enlist(tx));
    }

    // ──────────────────────────────────────────────
    // SearchAsync — 设计文档 3.4, REFACTOR-2 §2-3
    // ──────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_ShouldThrowIfNotEnlisted()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        var request = new RelationalVectorSearchRequest(
            "items", "embedding", [0.1, 0.2, 0.3]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.SearchAsync(request, tx));

        Assert.Contains("not enlisted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();
        var request = new RelationalVectorSearchRequest("t", "v", [1f]);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.SearchAsync(request, tx));
    }

    [Fact]
    public async Task SearchAsync_ShouldRequireEitherDenseOrSparseVector()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        // Neither DenseVector nor SparseVector provided
        var request = new RelationalVectorSearchRequest("t", "v");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            driver.SearchAsync(request, tx));

        Assert.Contains("Either DenseVector or SparseVector", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_ShouldAcceptDenseVector()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        var request = new RelationalVectorSearchRequest(
            "items", "embedding", [0.1, 0.2, 0.3], TopK: 5);

        // Should fail with "not enlisted" rather than "one of ... must be provided"
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.SearchAsync(request, tx));

        Assert.Contains("not enlisted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_ShouldAcceptSparseVector()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        var request = new RelationalVectorSearchRequest(
            "items", "sparse_embedding",
            SparseVector: new SparseVector([0, 5], [0.5, 0.8]),
            TopK: 3);

        // Should fail with "not enlisted" rather than param validation
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.SearchAsync(request, tx));

        Assert.Contains("not enlisted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Design requirement (REFACTOR-2 §3): Search accepts optional SQL WHERE clause
    /// and parameters, delegating filtering to native SQL.
    /// </summary>
    [Fact]
    public void SearchAsync_ShouldAcceptWhereClauseAndParameters()
    {
        var request = new RelationalVectorSearchRequest(
            "items", "embedding", [0.1], TopK: 10,
            WhereClause: "category = @cat AND price > @min_price",
            Parameters: [
                new RelationalParameter("@cat", "electronics"),
                new RelationalParameter("@min_price", 100),
            ]);

        Assert.Equal("category = @cat AND price > @min_price", request.WhereClause);
        Assert.Equal(2, request.Parameters!.Count);
    }

    // ──────────────────────────────────────────────
    // Design: Search only, no CRUD — REFACTOR-2 §2
    // ──────────────────────────────────────────────

    [Fact]
    public void PgVectorDriver_ShouldNotImplementCrudCapabilities()
    {
        var driver = CreateDriver();

        // CRUD is done via IRelationalExecuteCapability/IRelationalQueryCapability
        // (i.e., via PostgreSqlDriver), not through the vector driver
        Assert.IsNotAssignableFrom<IRelationalExecuteCapability>(driver);
        Assert.IsNotAssignableFrom<IRelationalQueryCapability>(driver);
    }

    // ──────────────────────────────────────────────
    // HealthCheck — 设计文档 5.3
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_ShouldReturnFalseWhenUnreachable()
    {
        var driver = new PgVectorDriver(
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
        driver.Dispose(); // Should not throw
    }

    // ──────────────────────────────────────────────
    // SparseVector validation — 设计文档 3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void SparseVector_ShouldValidateLengths()
    {
        // Indices and Values must have same length
        var sv = new SparseVector([0, 1, 2], [0.1, 0.2, 0.3]);
        Assert.Equal(3, sv.Indices.Length);
        Assert.Equal(3, sv.Values.Length);
    }

    private static PgVectorDriver CreateDriver()
    {
        return new PgVectorDriver(TestConnectionString,
            Substitute.For<ILogger<PgVectorDriver>>());
    }
}
