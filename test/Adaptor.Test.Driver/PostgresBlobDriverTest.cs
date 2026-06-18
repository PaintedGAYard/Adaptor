namespace Adaptor.Test.Driver;

/// <summary>
/// Tests for <see cref="PostgresBlobDriver"/>.
/// Design-based: derived from DETAILED-DESIGN.md §5.2–5.3, BLOB-STREAM-DESIGN.md §6, §9.
/// </summary>
public sealed class PostgresBlobDriverTest
{
    private const string TestConnectionString =
        "Host=localhost;Database=adaptor_test;Username=test;Password=test";

    // ──────────────────────────────────────────────
    // IResourceManager compliance — 设计文档 5.2, BLOB-STREAM §6
    // ──────────────────────────────────────────────

    [Fact]
    public void PostgresBlobDriver_ShouldImplementAllRequiredInterfaces()
    {
        var driver = CreateDriver();

        Assert.IsAssignableFrom<IResourceManager>(driver);
        Assert.IsAssignableFrom<ITransactionalResourceManager>(driver);
        Assert.IsAssignableFrom<IBlobUploadCapability>(driver);
        Assert.IsAssignableFrom<IBlobDownloadCapability>(driver);
        Assert.IsAssignableFrom<IBlobRandomAccessCapability>(driver);
        Assert.IsAssignableFrom<IHealthCheckCapability>(driver);
        Assert.IsAssignableFrom<IDisposable>(driver);
    }

    [Fact]
    public void PostgresBlobDriver_ShouldHaveCorrectResourceType()
    {
        var driver = CreateDriver();
        Assert.Equal(ResourceType.Blob, driver.ResourceType);
    }

    [Fact]
    public void PostgresBlobDriver_ShouldHaveUniqueIdentifier()
    {
        var d1 = CreateDriver();
        var d2 = CreateDriver();
        Assert.NotEqual(d1.ResourceManagerIdentifier, d2.ResourceManagerIdentifier);
    }

    // ──────────────────────────────────────────────
    // Enlist — 设计文档 5.2, BLOB-STREAM §9.3
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
    // IBlobUploadCapability — 设计文档 §3.4, BLOB-STREAM §6
    // ──────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();
        var request = new BlobUploadRequest("key", [1, 2, 3]);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.UploadAsync(request, tx));
    }

    [Fact]
    public async Task UploadAsync_ShouldRequireNonNullRequest()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.UploadAsync(null!, tx));
    }

    [Fact]
    public async Task UploadAsync_ShouldRequireNonNullTransaction()
    {
        var driver = CreateDriver();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.UploadAsync(new BlobUploadRequest("k", []), null!));
    }

    // ──────────────────────────────────────────────
    // IBlobDownloadCapability — 设计文档 §3.4, BLOB-STREAM §6
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();
        var request = new BlobDownloadRequest("key");

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.DownloadAsync(request, tx));
    }

    [Fact]
    public async Task DownloadAsync_ShouldRequireNonNullRequest()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.DownloadAsync(null!, tx));
    }

    [Fact]
    public async Task DownloadAsync_ShouldReturnErrorForNonexistentKey()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        // Without being enlisted, this should throw InvalidOperationException
        // (not a "key not found" — that requires successful enlistment first)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.DownloadAsync(new BlobDownloadRequest("nonexistent"), tx));

        Assert.Contains("not enlisted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // IBlobRandomAccessCapability — BLOB-STREAM §5.1, §6.1
    // ──────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.OpenAsync("key", BlobAccessMode.Read, tx));
    }

    [Fact]
    public async Task CloseAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.CloseAsync(1, tx));
    }

    [Fact]
    public async Task ReadAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.ReadAsync(1, 1024, tx));
    }

    [Fact]
    public async Task WriteAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.WriteAsync(1, [1, 2, 3], tx));
    }

    [Fact]
    public async Task SeekAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.SeekAsync(1, 0, SeekOrigin.Begin, tx));
    }

    [Fact]
    public async Task TruncateAsync_ShouldThrowIfDisposed()
    {
        var driver = CreateDriver();
        driver.Dispose();

        using var tx = new CommittableTransaction();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            driver.TruncateAsync(1, 0, tx));
    }

    // ──────────────────────────────────────────────
    // Null guards for all random-access methods — BLOB-STREAM §5
    // ──────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_ShouldRequireNonNullTransaction()
    {
        var driver = CreateDriver();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.OpenAsync("key", BlobAccessMode.Read, null!));
    }

    [Fact]
    public async Task CloseAsync_ShouldRequireNonNullTransaction()
    {
        var driver = CreateDriver();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.CloseAsync(1, null!));
    }

    [Fact]
    public async Task ReadAsync_ShouldRequireNonNullTransaction()
    {
        var driver = CreateDriver();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.ReadAsync(1, 1024, null!));
    }

    [Fact]
    public async Task WriteAsync_ShouldRequireNonNullTransaction()
    {
        var driver = CreateDriver();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.WriteAsync(1, [1, 2, 3], null!));
    }

    [Fact]
    public async Task WriteAsync_ShouldRequireNonNullData()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.WriteAsync(1, null!, tx));
    }

    [Fact]
    public async Task SeekAsync_ShouldRequireNonNullTransaction()
    {
        var driver = CreateDriver();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.SeekAsync(1, 0, SeekOrigin.Begin, null!));
    }

    [Fact]
    public async Task TruncateAsync_ShouldRequireNonNullTransaction()
    {
        var driver = CreateDriver();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.TruncateAsync(1, 0, null!));
    }

    // ──────────────────────────────────────────────
    // BlobAccessMode — BLOB-STREAM §3.4, §6.2
    // ──────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_WithCreateModeOnUnenlistedTx_ShouldThrowInvalidOp()
    {
        var driver = CreateDriver();
        using var tx = new CommittableTransaction();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.OpenAsync("new-key", BlobAccessMode.Create, tx));

        Assert.Contains("not enlisted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // HealthCheck — 设计文档 5.3
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_ShouldReturnFalseWhenUnreachable()
    {
        var driver = new PostgresBlobDriver(
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

    private static PostgresBlobDriver CreateDriver()
    {
        return new PostgresBlobDriver(TestConnectionString,
            Substitute.For<ILogger<PostgresBlobDriver>>());
    }
}
