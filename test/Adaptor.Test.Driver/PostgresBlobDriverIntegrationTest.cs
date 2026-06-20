using Npgsql;
using Testcontainers.PostgreSql;

namespace Adaptor.Test.Driver;

/// <summary>
/// Integration tests for <see cref="PostgresBlobDriver"/> using Testcontainers.
/// Requires Docker. Verifies actual BLOB upload/download and large object
/// operations against a real PostgreSQL database.
/// </summary>
public sealed class PostgresBlobDriverIntegrationTest : IAsyncLifetime
{
    private PostgreSqlContainer? _pgContainer;

    [Fact]
    public async Task UploadAsync_ShouldStoreAndBeDownloadable()
    {
        // Arrange
        var data = "Hello, Adaptor BLOB!"u8.ToArray();
        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new BlobUploadRequest("test-key", data, "text/plain",
            new Dictionary<string, string> { ["source"] = "integration-test" });

        // Act
        var result = await driver.UploadAsync(request, tx);

        // Assert — upload succeeded
        Assert.Null(result.ErrorMessage);
        Assert.Equal("test-key", result.Key);
        Assert.Equal(data.LongLength, result.Size);

        // Verify by downloading within the same transaction
        var downloadResult = await driver.DownloadAsync(new BlobDownloadRequest("test-key"), tx);
        Assert.Null(downloadResult.ErrorMessage);
        Assert.Equal(data, downloadResult.Data);
        Assert.Equal("text/plain", downloadResult.ContentType);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task UploadAndDownloadAsync_ShouldRoundTrip()
    {
        // Arrange
        var originalData = new byte[1024];
        new Random(42).NextBytes(originalData);

        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var uploadRequest = new BlobUploadRequest("roundtrip-key", originalData, "application/octet-stream");

        var uploadResult = await driver.UploadAsync(uploadRequest, tx);
        Assert.Null(uploadResult.ErrorMessage);

        // Act — download
        var downloadRequest = new BlobDownloadRequest("roundtrip-key");
        var downloadResult = await driver.DownloadAsync(downloadRequest, tx);

        // Assert
        Assert.Null(downloadResult.ErrorMessage);
        Assert.Equal("application/octet-stream", downloadResult.ContentType);
        Assert.Equal(originalData, downloadResult.Data);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task DownloadAsync_ForNonexistentKey_ShouldReturnError()
    {
        // Arrange
        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var request = new BlobDownloadRequest("nonexistent-key");

        // Act
        var result = await driver.DownloadAsync(request, tx);

        // Assert — key not found is not an exception, returns error message
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task RandomAccess_ShouldReadWriteSeek()
    {
        // Arrange
        var data = "Random access test data"u8.ToArray();
        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        // Upload first so the key exists
        var uploadResult = await driver.UploadAsync(
            new BlobUploadRequest("random-key", data), tx);
        Assert.Null(uploadResult.ErrorMessage);
        Assert.Equal(data.Length, uploadResult.Size);

        // Act — open and read
        var openResult = await driver.OpenAsync("random-key", BlobAccessMode.Read, tx);
        Assert.Null(openResult.ErrorMessage);
        Assert.True(openResult.LoFd > 0);
        Assert.Equal(data.Length, openResult.BlobSize);

        var loFd = openResult.LoFd;

        var readResult = await driver.ReadAsync(loFd, 1024, tx);
        Assert.Null(readResult.ErrorMessage);

        // The read data should match
        Assert.True(readResult.BytesRead > 0);
        Assert.Equal(data, readResult.Data);

        // Seek back to beginning
        var newOffset = await driver.SeekAsync(loFd, 0, SeekOrigin.Begin, tx);
        Assert.Equal(0, newOffset);

        // Close
        await driver.CloseAsync(loFd, tx);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task MultipleBlobs_ShouldBeIndependent()
    {
        // Arrange
        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        var dataA = "Data for blob A"u8.ToArray();
        var dataB = "Data for blob B is different"u8.ToArray();

        // Upload both
        var resultA = await driver.UploadAsync(new BlobUploadRequest("blob-A", dataA), tx);
        Assert.Null(resultA.ErrorMessage);

        var resultB = await driver.UploadAsync(new BlobUploadRequest("blob-B", dataB), tx);
        Assert.Null(resultB.ErrorMessage);

        // Download both and verify independence
        var downloadA = await driver.DownloadAsync(new BlobDownloadRequest("blob-A"), tx);
        Assert.Equal(dataA, downloadA.Data);

        var downloadB = await driver.DownloadAsync(new BlobDownloadRequest("blob-B"), tx);
        Assert.Equal(dataB, downloadB.Data);

        tx.Rollback();
        driver.Dispose();
    }

    /// <summary>
    /// Design: CloseAsync should release the server-side LO descriptor.
    /// After closing, reading via the same loFd should fail.
    /// </summary>
    [Fact]
    public async Task CloseAsync_ShouldReleaseDescriptor()
    {
        // Arrange
        var data = "Close test"u8.ToArray();
        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        await driver.UploadAsync(new BlobUploadRequest("close-key", data), tx);
        var openResult = await driver.OpenAsync("close-key", BlobAccessMode.Read, tx);
        Assert.Null(openResult.ErrorMessage);
        var loFd = openResult.LoFd;

        // Act — close the descriptor
        await driver.CloseAsync(loFd, tx);

        // Assert — reading from a closed descriptor should fail
        var readResult = await driver.ReadAsync(loFd, 1024, tx);
        Assert.NotNull(readResult.ErrorMessage);
        Assert.Equal(0, readResult.BytesRead);

        tx.Rollback();
        driver.Dispose();
    }

    /// <summary>
    /// Design: SeekAsync should reposition the read/write offset.
    /// After Seek(Begin, 5), ReadAsync(1024) should return bytes starting at offset 5.
    /// </summary>
    [Fact]
    public async Task SeekAsync_ShouldAffectSubsequentReadPosition()
    {
        // Arrange
        var data = "0123456789ABCDEF"u8.ToArray();
        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        await driver.UploadAsync(new BlobUploadRequest("seek-key", data), tx);
        var openResult = await driver.OpenAsync("seek-key", BlobAccessMode.Read, tx);
        Assert.Null(openResult.ErrorMessage);

        // Act — seek to offset 5
        var newOffset = await driver.SeekAsync(openResult.LoFd, 5, SeekOrigin.Begin, tx);
        Assert.Equal(5, newOffset);

        // Read — should return bytes from offset 5 onwards
        var readResult = await driver.ReadAsync(openResult.LoFd, 1024, tx);
        Assert.Null(readResult.ErrorMessage);
        Assert.Equal(data.Length - 5, readResult.BytesRead);
        Assert.Equal(data[5..], readResult.Data);

        tx.Rollback();
        driver.Dispose();
    }

    /// <summary>
    /// Design: WriteAsync should write at the current seek position and advance the offset.
    /// After Seek(5), Write(data) should write starting at offset 5, not offset 0.
    /// </summary>
    [Fact]
    public async Task WriteAsync_ShouldRespectSeekPosition()
    {
        // Arrange
        var data = "XXXXXYYYYY"u8.ToArray();
        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        await driver.UploadAsync(new BlobUploadRequest("write-key", new byte[] { (byte)'B', (byte)'B', (byte)'B', (byte)'B', (byte)'B', (byte)'B', (byte)'B', (byte)'B', (byte)'B', (byte)'B' }), tx);
        var openResult = await driver.OpenAsync("write-key", BlobAccessMode.Write, tx);
        Assert.Null(openResult.ErrorMessage);

        // Act — seek to offset 5, then write "YYYYY"
        await driver.SeekAsync(openResult.LoFd, 5, SeekOrigin.Begin, tx);
        await driver.WriteAsync(openResult.LoFd, new byte[] { (byte)'Y', (byte)'Y', (byte)'Y', (byte)'Y', (byte)'Y' }, tx);

        // Assert — final blob should be "BBBBBYYYYY"
        var download = await driver.DownloadAsync(new BlobDownloadRequest("write-key"), tx);
        Assert.Null(download.ErrorMessage);
        var expected = new byte[] { (byte)'B', (byte)'B', (byte)'B', (byte)'B', (byte)'B', (byte)'Y', (byte)'Y', (byte)'Y', (byte)'Y', (byte)'Y' };
        Assert.Equal(expected, download.Data);

        tx.Rollback();
        driver.Dispose();
    }

    /// <summary>
    /// Design: TruncateAsync should shorten the large object to the given length.
    /// After Truncate(3), reading the full blob should return only 3 bytes.
    /// </summary>
    [Fact]
    public async Task TruncateAsync_ShouldShortenBlob()
    {
        // Arrange
        var data = "Truncate test data"u8.ToArray();
        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        using var tx = new CommittableTransaction();
        driver.Enlist(tx);

        await driver.UploadAsync(new BlobUploadRequest("trunc-key", data), tx);
        var openResult = await driver.OpenAsync("trunc-key", BlobAccessMode.Read, tx);
        Assert.Null(openResult.ErrorMessage);

        // Act — truncate to 3 bytes
        await driver.TruncateAsync(openResult.LoFd, 3, tx);

        // Assert — only first 3 bytes remain
        var download = await driver.DownloadAsync(new BlobDownloadRequest("trunc-key"), tx);
        Assert.Null(download.ErrorMessage);
        Assert.Equal(3, download.Data.Length);
        var expectedTrunc = new byte[] { (byte)'T', (byte)'r', (byte)'u' };
        Assert.Equal(expectedTrunc, download.Data);

        tx.Rollback();
        driver.Dispose();
    }

    [Fact]
    public async Task HealthCheck_ShouldReturnTrueForReachableDatabase()
    {
        // Arrange
        var driver = new PostgresBlobDriver(_pgContainer!.GetConnectionString(),
            Substitute.For<ILogger<PostgresBlobDriver>>());

        // Act
        var healthy = await driver.HealthCheckAsync();

        // Assert
        Assert.True(healthy);

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
}
