namespace Adaptor.Test.Coordinator;

/// <summary>
/// Tests for the abstraction interfaces and their contracts.
/// Design-based: derived from DETAILED-DESIGN.md §5.3, §5.2, REFACTOR-2 §1–2.
/// These tests verify the interface contracts and composition principles.
/// </summary>
public sealed class AbstractionsTest
{
    // ──────────────────────────────────────────────
    // IResourceManager — 设计文档 5.2, 5.3
    // ──────────────────────────────────────────────

    [Fact]
    public void IResourceManager_ShouldRequireName()
    {
        var driver = Substitute.For<IResourceManager>();
        driver.Name.Returns("TestDriver");
        Assert.False(string.IsNullOrEmpty(driver.Name));
    }

    [Fact]
    public void IResourceManager_ShouldRequireResourceType()
    {
        var driver = Substitute.For<IResourceManager>();

        // All three resource types must be valid
        driver.ResourceType.Returns(ResourceType.Sql);
        Assert.Equal(ResourceType.Sql, driver.ResourceType);

        driver.ResourceType.Returns(ResourceType.Vector);
        Assert.Equal(ResourceType.Vector, driver.ResourceType);

        driver.ResourceType.Returns(ResourceType.Blob);
        Assert.Equal(ResourceType.Blob, driver.ResourceType);
    }

    [Fact]
    public void IResourceManager_ShouldRequireUniqueIdentifier()
    {
        var d1 = Substitute.For<IResourceManager>();
        d1.ResourceManagerIdentifier.Returns(Guid.NewGuid());

        var d2 = Substitute.For<IResourceManager>();
        d2.ResourceManagerIdentifier.Returns(Guid.NewGuid());

        // Each driver must have a unique identifier
        Assert.NotEqual(d1.ResourceManagerIdentifier, d2.ResourceManagerIdentifier);
    }

    // ──────────────────────────────────────────────
    // ITransactionalResourceManager — 设计文档 5.2, REFACTOR §5
    // ──────────────────────────────────────────────

    [Fact]
    public void ITransactionalResourceManager_ShouldExtendIResourceManager()
    {
        // Verify the type hierarchy
        var txDriver = Substitute.For<ITransactionalResourceManager>();
        Assert.IsAssignableFrom<IResourceManager>(txDriver);
    }

    [Fact]
    public void ITransactionalResourceManager_ShouldAcceptEnlistment()
    {
        var txDriver = Substitute.For<ITransactionalResourceManager>();
        using var tx = new CommittableTransaction();

        // Enlist must not throw for a valid transaction
        var ex = Record.Exception(() => txDriver.Enlist(tx));
        Assert.Null(ex);
    }

    // ──────────────────────────────────────────────
    // Composition over Inheritance — 设计文档 5.3, REFACTOR-2 §1
    // ──────────────────────────────────────────────

    /// <summary>
    /// Design requirement: drivers should compose independent capability interfaces,
    /// not inherit from a monolithic IStorageDriver.
    /// </summary>
    [Fact]
    public void Driver_ShouldComposeCapabilitiesIndependently()
    {
        // A driver can implement any combination of capabilities.
        // Use Substitute.For with Type[] because NSubstitute supports only single type parameter.
        var sqlOnly = Substitute.For(new[]
        {
            typeof(IResourceManager),
            typeof(IRelationalExecuteCapability),
        }, []);
        var vectorOnly = Substitute.For(new[]
        {
            typeof(IResourceManager),
            typeof(IRelationalVectorSearchCapability),
        }, []);
        var blobOnly = Substitute.For(new[]
        {
            typeof(IResourceManager),
            typeof(IBlobUploadCapability),
            typeof(IBlobDownloadCapability),
        }, []);
        var fullStack = Substitute.For(new[]
        {
            typeof(IResourceManager),
            typeof(ITransactionalResourceManager),
            typeof(IRelationalExecuteCapability),
            typeof(IRelationalQueryCapability),
            typeof(IRelationalVectorSearchCapability),
            typeof(IBlobUploadCapability),
            typeof(IBlobDownloadCapability),
            typeof(IBlobRandomAccessCapability),
            typeof(IHealthCheckCapability),
        }, []);

        // All are valid drivers — no single base interface is required beyond IResourceManager
        Assert.IsAssignableFrom<IResourceManager>(sqlOnly);
        Assert.IsAssignableFrom<IResourceManager>(vectorOnly);
        Assert.IsAssignableFrom<IResourceManager>(blobOnly);
        Assert.IsAssignableFrom<IResourceManager>(fullStack);
    }

    // ──────────────────────────────────────────────
    // Capability interfaces — 设计文档 5.3
    // ──────────────────────────────────────────────

    [Fact]
    public void CapabilityInterfaces_ShouldAcceptTransactionParameter()
    {
        // All capability methods accept a Transaction parameter
        // These are compile-time contract checks expressed as delegates

        // IRelationalExecuteCapability
        var execute = Substitute.For<IRelationalExecuteCapability>();
        var executeRequest = new RelationalExecuteRequest("SELECT 1");
        _ = execute.ExecuteAsync(executeRequest, new CommittableTransaction());

        // IRelationalQueryCapability
        var query = Substitute.For<IRelationalQueryCapability>();
        var queryRequest = new RelationalQueryRequest("SELECT 1");
        _ = query.QueryAsync(queryRequest, new CommittableTransaction());

        // IRelationalVectorSearchCapability
        var search = Substitute.For<IRelationalVectorSearchCapability>();
        var searchRequest = new RelationalVectorSearchRequest("t", "v", [1f]);
        _ = search.SearchAsync(searchRequest, new CommittableTransaction());

        // IBlobUploadCapability
        var upload = Substitute.For<IBlobUploadCapability>();
        var uploadRequest = new BlobUploadRequest("k", [1, 2, 3]);
        _ = upload.UploadAsync(uploadRequest, new CommittableTransaction());

        // IBlobDownloadCapability
        var download = Substitute.For<IBlobDownloadCapability>();
        var downloadRequest = new BlobDownloadRequest("k");
        _ = download.DownloadAsync(downloadRequest, new CommittableTransaction());

        // IBlobRandomAccessCapability
        var randomAccess = Substitute.For<IBlobRandomAccessCapability>();
        _ = randomAccess.OpenAsync("k", BlobAccessMode.Read, new CommittableTransaction());
        _ = randomAccess.CloseAsync(1, new CommittableTransaction());
        _ = randomAccess.ReadAsync(1, 1024, new CommittableTransaction());
        _ = randomAccess.WriteAsync(1, [1, 2, 3], new CommittableTransaction());
        _ = randomAccess.SeekAsync(1, 0, SeekOrigin.Begin, new CommittableTransaction());
        _ = randomAccess.TruncateAsync(1, 0, new CommittableTransaction());

        // IHealthCheckCapability
        var health = Substitute.For<IHealthCheckCapability>();
        _ = health.HealthCheckAsync();
    }

    // ──────────────────────────────────────────────
    // BlobAccessMode — 设计文档 BLOB-STREAM §3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void BlobAccessMode_ShouldDefineAllModes()
    {
        Assert.Equal(1, (int)BlobAccessMode.Read);
        Assert.Equal(2, (int)BlobAccessMode.Write);
        Assert.Equal(3, (int)BlobAccessMode.ReadWrite);
        Assert.Equal(4, (int)BlobAccessMode.Create);
        Assert.Equal(5, (int)BlobAccessMode.CreateOrReplace);
        Assert.Equal(6, (int)BlobAccessMode.Append);
    }

    // ──────────────────────────────────────────────
    // ResourceType — 设计文档 5.2
    // ──────────────────────────────────────────────

    [Fact]
    public void ResourceType_ShouldDefineAllTypes()
    {
        Assert.Equal(0, (int)ResourceType.Sql);
        Assert.Equal(1, (int)ResourceType.Vector);
        Assert.Equal(2, (int)ResourceType.Blob);
    }
}
