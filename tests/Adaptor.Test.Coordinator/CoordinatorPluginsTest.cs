namespace Adaptor.Test.Coordinator;

/// <summary>
/// Tests for <see cref="TransactionPlugin"/>, <see cref="RelationalPlugin"/>, <see cref="VectorSearchPlugin"/>.
/// Design-based: derived from SK-PLUGIN minutes §1, §3.
/// These tests verify that plugins forward calls to the Coordinator correctly.
/// </summary>
public sealed class CoordinatorPluginsTest
{
    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static TransactionCoordinator CreateCoordinator(
        IEnumerable<IResourceManager>? drivers = null)
    {
        return new TransactionCoordinator(
            drivers ?? [],
            Substitute.For<SessionManager>(
                Options.Create(new CoordinatorOptions()),
                Substitute.For<ILogger<SessionManager>>()),
            Options.Create(new CoordinatorOptions()),
            Substitute.For<ILogger<TransactionCoordinator>>());
    }

    // ──────────────────────────────────────────────
    // TransactionPlugin — SK-PLUGIN §3
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TransactionPlugin_BeginTransaction_ShouldForwardToCoordinator()
    {
        var coordinator = CreateCoordinator();
        var plugin = new TransactionPlugin(
            coordinator,
            Substitute.For<ILogger<TransactionPlugin>>());

        var result = await plugin.BeginTransactionAsync();

        Assert.NotNull(result.Transaction);
        Assert.IsType<CommittableTransaction>(result.Transaction);
    }

    [Fact]
    public async Task TransactionPlugin_BeginTransaction_ShouldAcceptOptionalTimeout()
    {
        var coordinator = CreateCoordinator();
        var plugin = new TransactionPlugin(coordinator, Substitute.For<ILogger<TransactionPlugin>>());

        var result = await plugin.BeginTransactionAsync(TimeSpan.FromSeconds(60));

        var expectedExpiry = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        Assert.InRange(result.ExpiresAt, expectedExpiry.AddSeconds(-2), expectedExpiry.AddSeconds(2));
    }

    [Fact]
    public async Task TransactionPlugin_CommitTransaction_ShouldForwardToCoordinator()
    {
        var coordinator = CreateCoordinator();
        var plugin = new TransactionPlugin(coordinator, Substitute.For<ILogger<TransactionPlugin>>());

        var beginResult = await plugin.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var commitResult = await plugin.CommitTransactionAsync(txId);

        Assert.Equal(CommitStatus.Committed, commitResult.Status);
    }

    [Fact]
    public async Task TransactionPlugin_RollbackTransaction_ShouldForwardToCoordinator()
    {
        var coordinator = CreateCoordinator();
        var plugin = new TransactionPlugin(coordinator, Substitute.For<ILogger<TransactionPlugin>>());

        var beginResult = await plugin.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        await plugin.RollbackTransactionAsync(txId);

        Assert.Equal(TransactionStatus.Aborted, beginResult.Transaction.TransactionInformation.Status);
    }

    // ──────────────────────────────────────────────
    // TransactionPlugin — SK function attributes
    // ──────────────────────────────────────────────

    [Fact]
    public void TransactionPlugin_ShouldHaveKernelFunctionAttributes()
    {
        var methods = typeof(TransactionPlugin).GetMethods();

        Assert.Contains(methods, m =>
            m.Name == "BeginTransactionAsync" &&
            m.GetCustomAttributes(typeof(Microsoft.SemanticKernel.KernelFunctionAttribute), false).Length > 0);

        Assert.Contains(methods, m =>
            m.Name == "CommitTransactionAsync" &&
            m.GetCustomAttributes(typeof(Microsoft.SemanticKernel.KernelFunctionAttribute), false).Length > 0);

        Assert.Contains(methods, m =>
            m.Name == "RollbackTransactionAsync" &&
            m.GetCustomAttributes(typeof(Microsoft.SemanticKernel.KernelFunctionAttribute), false).Length > 0);
    }

    // ──────────────────────────────────────────────
    // RelationalPlugin — SK-PLUGIN §3
    // ──────────────────────────────────────────────

    private sealed class FakeRelationalDriver :
        IResourceManager, ITransactionalResourceManager,
        IRelationalExecuteCapability, IRelationalQueryCapability
    {
        public string Name => "FakeRelational";
        public ResourceType ResourceType => ResourceType.Sql;
        public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();

        public void Enlist(Transaction transaction) { }

        public Task<RelationalExecuteResult> ExecuteAsync(
            RelationalExecuteRequest request, Transaction transaction, CancellationToken ct)
            => Task.FromResult(new RelationalExecuteResult(1, TimeSpan.Zero));

        public Task<RelationalQueryResult> QueryAsync(
            RelationalQueryRequest request, Transaction transaction, CancellationToken ct)
            => Task.FromResult(new RelationalQueryResult(
                [], TimeSpan.Zero));
    }

    [Fact]
    public async Task RelationalPlugin_Execute_ShouldForwardToCoordinator()
    {
        var coordinator = CreateCoordinator(drivers: [new FakeRelationalDriver()]);
        var plugin = new RelationalPlugin(coordinator, Substitute.For<ILogger<RelationalPlugin>>());

        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var result = await plugin.ExecuteAsync(txId, "INSERT INTO test VALUES (1)");

        Assert.Equal(1, result.AffectedRows);
    }

    [Fact]
    public async Task RelationalPlugin_Query_ShouldForwardToCoordinator()
    {
        var coordinator = CreateCoordinator(drivers: [new FakeRelationalDriver()]);
        var plugin = new RelationalPlugin(coordinator, Substitute.For<ILogger<RelationalPlugin>>());

        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var result = await plugin.QueryAsync(txId, "SELECT * FROM test");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task RelationalPlugin_ExecuteBatch_ShouldExecuteMultipleCommands()
    {
        var coordinator = CreateCoordinator(drivers: [new FakeRelationalDriver()]);
        var plugin = new RelationalPlugin(coordinator, Substitute.For<ILogger<RelationalPlugin>>());

        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var totalAffected = await plugin.ExecuteBatchAsync(txId, [
            new RelationalPlugin.BatchItem("INSERT INTO t VALUES (1)"),
            new RelationalPlugin.BatchItem("INSERT INTO t VALUES (2)"),
        ]);

        Assert.Equal(2, totalAffected);
    }

    [Fact]
    public void RelationalPlugin_ShouldHaveKernelFunctionAttributes()
    {
        var methods = typeof(RelationalPlugin).GetMethods();
        Assert.Contains(methods, m =>
            m.Name == "ExecuteAsync" &&
            m.GetCustomAttributes(typeof(Microsoft.SemanticKernel.KernelFunctionAttribute), false).Length > 0);
        Assert.Contains(methods, m =>
            m.Name == "QueryAsync" &&
            m.GetCustomAttributes(typeof(Microsoft.SemanticKernel.KernelFunctionAttribute), false).Length > 0);
        Assert.Contains(methods, m =>
            m.Name == "ExecuteBatchAsync" &&
            m.GetCustomAttributes(typeof(Microsoft.SemanticKernel.KernelFunctionAttribute), false).Length > 0);
    }

    // ──────────────────────────────────────────────
    // VectorSearchPlugin — SK-PLUGIN §3, REFACTOR-2 §2-3
    // ──────────────────────────────────────────────

    private sealed class FakeVectorDriver :
        IResourceManager, ITransactionalResourceManager,
        IRelationalVectorSearchCapability
    {
        public string Name => "FakeVector";
        public ResourceType ResourceType => ResourceType.Vector;
        public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();

        public void Enlist(Transaction transaction) { }

        public Task<VectorSearchResult> SearchAsync(
            RelationalVectorSearchRequest request, Transaction transaction, CancellationToken ct)
            => Task.FromResult(new VectorSearchResult(
                [], TimeSpan.Zero));
    }

    [Fact]
    public async Task VectorSearchPlugin_Search_ShouldForwardToCoordinator()
    {
        var coordinator = CreateCoordinator(drivers: [new FakeVectorDriver()]);
        var plugin = new VectorSearchPlugin(coordinator, Substitute.For<ILogger<VectorSearchPlugin>>());

        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var result = await plugin.SearchAsync(txId, "my_table", "embedding",
            denseVector: [0.1f, 0.2f, 0.3f]);

        Assert.NotNull(result);
    }

    [Fact]
    public void VectorSearchPlugin_ShouldHaveKernelFunctionAttribute()
    {
        var method = typeof(VectorSearchPlugin).GetMethod("SearchAsync");
        Assert.NotNull(method);
        Assert.Single(method.GetCustomAttributes(typeof(Microsoft.SemanticKernel.KernelFunctionAttribute), false));
    }
}
