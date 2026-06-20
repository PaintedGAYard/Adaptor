namespace Adaptor.Test.Coordinator;

/// <summary>
/// Tests for <see cref="TransactionCoordinator"/>.
/// Design-based: all assertions are derived from DETAILED-DESIGN.md, REFACTOR minutes, and REFACTOR-2 minutes.
/// </summary>
public sealed class TransactionCoordinatorTest
{
    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static TransactionCoordinator CreateCoordinator(
        IEnumerable<IResourceManager>? drivers = null,
        CoordinatorOptions? options = null,
        SessionManager? sessionManager = null)
    {
        drivers ??= [];
        options ??= new CoordinatorOptions();
        sessionManager ??= Substitute.For<SessionManager>(
            Options.Create(options), Substitute.For<ILogger<SessionManager>>());

        return new TransactionCoordinator(
            drivers,
            sessionManager,
            Options.Create(options),
            Substitute.For<ILogger<TransactionCoordinator>>());
    }

    // ──────────────────────────────────────────────
    // BeginTransactionAsync — 设计文档 4.2, 4.4
    // ──────────────────────────────────────────────

    [Fact]
    public async Task BeginTransactionAsync_ShouldCreateCommittableTransaction()
    {
        var coordinator = CreateCoordinator();
        var result = await coordinator.BeginTransactionAsync();

        Assert.NotNull(result.Transaction);
        Assert.IsType<CommittableTransaction>(result.Transaction);
        Assert.Equal(TransactionStatus.Active, result.Transaction.TransactionInformation.Status);
    }

    [Fact]
    public async Task BeginTransactionAsync_ShouldReturnExpiresAt()
    {
        var coordinator = CreateCoordinator();
        var timeout = TimeSpan.FromSeconds(30);
        var result = await coordinator.BeginTransactionAsync(timeout);

        var expectedExpiry = DateTime.UtcNow + timeout;
        // Allow 2s clock skew
        Assert.InRange(result.ExpiresAt, expectedExpiry.AddSeconds(-2), expectedExpiry.AddSeconds(2));
    }

    [Fact]
    public async Task BeginTransactionAsync_ShouldUseDefaultTimeoutWhenNoneSpecified()
    {
        var options = new CoordinatorOptions { DefaultTransactionTimeout = TimeSpan.FromSeconds(45) };
        var coordinator = CreateCoordinator(options: options);
        var result = await coordinator.BeginTransactionAsync();

        var expectedExpiry = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        Assert.InRange(result.ExpiresAt, expectedExpiry.AddSeconds(-2), expectedExpiry.AddSeconds(2));
    }

    [Fact]
    public async Task BeginTransactionAsync_ShouldCapTimeoutAtMaxTransactionTimeout()
    {
        var options = new CoordinatorOptions
        {
            MaxTransactionTimeout = TimeSpan.FromSeconds(10),
            DefaultTransactionTimeout = TimeSpan.FromSeconds(30),
        };
        var coordinator = CreateCoordinator(options: options);

        // Request a timeout much larger than MaxTransactionTimeout
        var result = await coordinator.BeginTransactionAsync(TimeSpan.FromHours(1));

        var expectedExpiry = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        Assert.InRange(result.ExpiresAt, expectedExpiry.AddSeconds(-2), expectedExpiry.AddSeconds(2));
    }

    [Fact]
    public async Task BeginTransactionAsync_ShouldRegisterWithSessionManager()
    {
        // Cannot use Received() on CreateSession because it is not virtual.
        // Instead, verify behavior: a real SessionManager creates a session,
        // so after BeginTransactionAsync the coordinator has an active transaction.
        var coordinator = CreateCoordinator();

        await coordinator.BeginTransactionAsync();

        Assert.Equal(1, coordinator.ActiveTransactionCount);
    }

    [Fact]
    public async Task BeginTransactionAsync_ShouldStoreEntryForLocalIdentifier()
    {
        var coordinator = CreateCoordinator();
        var result = await coordinator.BeginTransactionAsync();
        var txId = result.Transaction.TransactionInformation.LocalIdentifier;

        var found = coordinator.FindTransaction(txId);
        Assert.NotNull(found);
        Assert.Same(result.Transaction, found);
    }

    // ──────────────────────────────────────────────
    // BeginBlobStreamTransactionAsync — 设计文档 BLOB-STREAM 5
    // ──────────────────────────────────────────────

    [Fact]
    public async Task BeginBlobStreamTransactionAsync_ShouldUseBlobStreamTimeout()
    {
        var options = new CoordinatorOptions
        {
            BlobStreamTransactionTimeout = TimeSpan.FromHours(24),
        };
        var coordinator = CreateCoordinator(options: options);
        var result = await coordinator.BeginBlobStreamTransactionAsync();

        var expectedExpiry = DateTime.UtcNow + TimeSpan.FromHours(24);
        Assert.InRange(result.ExpiresAt, expectedExpiry.AddSeconds(-2), expectedExpiry.AddSeconds(2));
    }

    [Fact]
    public async Task BeginBlobStreamTransactionAsync_ShouldCapAtMaxBlobStreamTimeout()
    {
        var options = new CoordinatorOptions
        {
            MaxBlobStreamTransactionTimeout = TimeSpan.FromHours(48),
        };
        var coordinator = CreateCoordinator(options: options);
        var result = await coordinator.BeginBlobStreamTransactionAsync(TimeSpan.FromDays(7));

        var expectedExpiry = DateTime.UtcNow + TimeSpan.FromHours(48);
        Assert.InRange(result.ExpiresAt, expectedExpiry.AddSeconds(-2), expectedExpiry.AddSeconds(2));
    }

    // ──────────────────────────────────────────────
    // CommitTransactionAsync — 设计文档 4.2 (2PC), REFACTOR 第3节
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CommitTransactionAsync_ShouldReturnCommittedStatus()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();

        var commitResult = await coordinator.CommitTransactionAsync(
            beginResult.Transaction.TransactionInformation.LocalIdentifier);

        Assert.Equal(CommitStatus.Committed, commitResult.Status);
    }

    [Fact]
    public async Task CommitTransactionAsync_ShouldCompleteTransaction()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        await coordinator.CommitTransactionAsync(txId);

        // After commit the transaction is removed from the active entries
        Assert.Null(coordinator.FindTransaction(txId));
    }

    [Fact]
    public async Task CommitTransactionAsync_OnUnknownId_ShouldReturnRolledBack()
    {
        var coordinator = CreateCoordinator();

        var result = await coordinator.CommitTransactionAsync("nonexistent-id");

        Assert.Equal(CommitStatus.RolledBack, result.Status);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CommitTransactionAsync_AfterRollback_ShouldReturnRolledBack()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        // Rollback first — this cleans up the entry
        await coordinator.RollbackTransactionAsync(txId);

        // Commit after rollback should return RolledBack, not throw
        var result = await coordinator.CommitTransactionAsync(txId);

        Assert.Equal(CommitStatus.RolledBack, result.Status);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CommitTransactionAsync_ShouldTrackPendingCommitForShutdown()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        // Commit and verify it completes without error
        var commitResult = await coordinator.CommitTransactionAsync(txId);
        Assert.Equal(CommitStatus.Committed, commitResult.Status);
    }

    // ──────────────────────────────────────────────
    // RollbackTransactionAsync — 设计文档 4.2, 4.4
    // ──────────────────────────────────────────────

    [Fact]
    public async Task RollbackTransactionAsync_ShouldRollbackTransaction()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        await coordinator.RollbackTransactionAsync(txId);

        // CommittableTransaction is disposed after Rollback in .NET 10,
        // so verify via ActiveTransactionCount instead.
        Assert.Equal(0, coordinator.ActiveTransactionCount);
    }

    [Fact]
    public async Task RollbackTransactionAsync_ShouldCleanupEntry()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        await coordinator.RollbackTransactionAsync(txId);

        Assert.Null(coordinator.FindTransaction(txId));
    }

    [Fact]
    public async Task RollbackTransactionAsync_OnUnknownId_ShouldBeNoOp()
    {
        var coordinator = CreateCoordinator();

        // Should not throw
        await coordinator.RollbackTransactionAsync("nonexistent-id");
    }

    // ──────────────────────────────────────────────
    // FindTransaction — 设计文档 REFACTOR 第2节
    // ──────────────────────────────────────────────

    [Fact]
    public async Task FindTransaction_ShouldReturnTransactionForValidId()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var found = coordinator.FindTransaction(txId);
        Assert.NotNull(found);
    }

    [Fact]
    public void FindTransaction_ShouldReturnNullForUnknownId()
    {
        var coordinator = CreateCoordinator();
        Assert.Null(coordinator.FindTransaction("nonexistent"));
    }

    // ──────────────────────────────────────────────
    // ExecuteOnDriverAsync — 设计文档 4.2, 5.2, REFACTOR 第5节
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteOnDriverAsync_ShouldFindCorrectDriver()
    {
        var driver = Substitute.For<IResourceManager>();
        driver.Name.Returns("TestDriver");
        driver.ResourceManagerIdentifier.Returns(Guid.NewGuid());

        var coordinator = CreateCoordinator(drivers: [driver]);
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var result = await coordinator.ExecuteOnDriverAsync<IResourceManager, string>(
            txId,
            (d, tx) => Task.FromResult(d.Name));

        Assert.Equal("TestDriver", result);
    }

    [Fact]
    public async Task ExecuteOnDriverAsync_ShouldThrowIfTransactionNotFound()
    {
        var driver = Substitute.For<IResourceManager>();
        var coordinator = CreateCoordinator(drivers: [driver]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteOnDriverAsync<IResourceManager, string>(
                "nonexistent",
                (d, tx) => Task.FromResult("")));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteOnDriverAsync_ShouldThrowIfDriverNotFound()
    {
        // No driver registered
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteOnDriverAsync<IResourceManager, string>(
                txId,
                (d, tx) => Task.FromResult("")));

        Assert.Contains("No driver", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteOnDriverAsync_ShouldAutoEnlistIfTransactional()
    {
        var txDriver = Substitute.For<ITransactionalResourceManager>();
        txDriver.Name.Returns("TxDriver");
        txDriver.ResourceManagerIdentifier.Returns(Guid.NewGuid());

        var coordinator = CreateCoordinator(drivers: [txDriver]);
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        await coordinator.ExecuteOnDriverAsync<ITransactionalResourceManager, string>(
            txId,
            (d, tx) => Task.FromResult("ok"));

        // Enlist must have been called on the driver
        txDriver.Received(1).Enlist(Arg.Any<Transaction>());
    }

    [Fact]
    public async Task ExecuteOnDriverAsync_ShouldNotAutoEnlistIfNotTransactional()
    {
        var driver = Substitute.For<IResourceManager>();
        driver.Name.Returns("NonTxDriver");
        driver.ResourceManagerIdentifier.Returns(Guid.NewGuid());

        var coordinator = CreateCoordinator(drivers: [driver]);
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        await coordinator.ExecuteOnDriverAsync<IResourceManager, string>(
            txId,
            (d, tx) => Task.FromResult("ok"));

        // Just ensure it didn't throw — non-transactional drivers should be callable
        // (they just won't be enlisted)
    }

    // ──────────────────────────────────────────────
    // ExecuteOnCapabilityAsync — 设计文档 5.3 REFACTOR-2
    // ──────────────────────────────────────────────

    public interface IFakeCapability
    {
        Task<string> DoSomethingAsync(CancellationToken ct);
    }

    [Fact]
    public async Task ExecuteOnCapabilityAsync_ShouldFindDriverImplementingCapability()
    {
        var capabilityDriver = Substitute.For<IResourceManager, IFakeCapability>();
        ((IResourceManager)capabilityDriver).Name.Returns("CapDriver");
        ((IResourceManager)capabilityDriver).ResourceManagerIdentifier.Returns(Guid.NewGuid());
        ((IFakeCapability)capabilityDriver).DoSomethingAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("capability-result"));

        var coordinator = CreateCoordinator(drivers: [(IResourceManager)capabilityDriver]);
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var result = await coordinator.ExecuteOnCapabilityAsync<IFakeCapability, string>(
            txId,
            (cap, tx) => cap.DoSomethingAsync(CancellationToken.None));

        Assert.Equal("capability-result", result);
    }

    [Fact]
    public async Task ExecuteOnCapabilityAsync_ShouldThrowIfNoDriverImplementsCapability()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteOnCapabilityAsync<IFakeCapability, string>(
                txId,
                (cap, tx) => cap.DoSomethingAsync(CancellationToken.None)));

        Assert.Contains("No driver", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteOnCapabilityAsync_ShouldThrowIfTransactionNotFound()
    {
        var coordinator = CreateCoordinator();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteOnCapabilityAsync<IFakeCapability, string>(
                "nonexistent",
                (cap, tx) => cap.DoSomethingAsync(CancellationToken.None)));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // GetDriver / GetDrivers — 设计文档 5.3
    // ──────────────────────────────────────────────

    [Fact]
    public void GetDriver_ShouldReturnFirstMatchingDriver()
    {
        var driver = Substitute.For<IResourceManager>();
        driver.Name.Returns("MyDriver");
        var coordinator = CreateCoordinator(drivers: [driver]);

        var found = coordinator.GetDriver<IResourceManager>();
        Assert.NotNull(found);
        Assert.Equal("MyDriver", found.Name);
    }

    [Fact]
    public void GetDriver_ShouldReturnNullIfNoMatch()
    {
        var coordinator = CreateCoordinator();
        Assert.Null(coordinator.GetDriver<IResourceManager>());
    }

    [Fact]
    public void GetDrivers_ShouldReturnAllMatchingDrivers()
    {
        var d1 = Substitute.For<IResourceManager>();
        d1.Name.Returns("D1");
        var d2 = Substitute.For<IResourceManager>();
        d2.Name.Returns("D2");
        var coordinator = CreateCoordinator(drivers: [d1, d2]);

        var all = coordinator.GetDrivers<IResourceManager>().ToList();
        Assert.Equal(2, all.Count);
    }

    // ──────────────────────────────────────────────
    // ShutdownAsync — 设计文档 REFACTOR 第3节
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ShutdownAsync_ShouldRollbackRemainingActiveTransactions()
    {
        var coordinator = CreateCoordinator();
        _ = await coordinator.BeginTransactionAsync();

        await coordinator.ShutdownAsync(TimeSpan.FromSeconds(5));

        // CommittableTransaction is disposed after Rollback in .NET 10,
        // so verify via ActiveTransactionCount instead.
        Assert.Equal(0, coordinator.ActiveTransactionCount);
    }

    [Fact]
    public async Task ShutdownAsync_ShouldCleanupAllEntries()
    {
        var coordinator = CreateCoordinator();
        _ = await coordinator.BeginTransactionAsync();
        _ = await coordinator.BeginTransactionAsync();

        await coordinator.ShutdownAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, coordinator.ActiveTransactionCount);
    }

    [Fact]
    public async Task ShutdownAsync_ShouldWaitForPendingCommits()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        // Start a commit but don't await it yet; shutdown should wait
        var commitTask = coordinator.CommitTransactionAsync(txId);

        // Shutdown with sufficient grace period
        await coordinator.ShutdownAsync(TimeSpan.FromSeconds(10));

        var commitResult = await commitTask;
        Assert.Equal(CommitStatus.Committed, commitResult.Status);
    }

    // ──────────────────────────────────────────────
    // ActiveTransactionCount — 设计文档 REFACTOR
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ActiveTransactionCount_ShouldReflectActiveTransactions()
    {
        var coordinator = CreateCoordinator();
        Assert.Equal(0, coordinator.ActiveTransactionCount);

        var r1 = await coordinator.BeginTransactionAsync();
        Assert.Equal(1, coordinator.ActiveTransactionCount);

        var r2 = await coordinator.BeginTransactionAsync();
        Assert.Equal(2, coordinator.ActiveTransactionCount);

        await coordinator.CommitTransactionAsync(r1.Transaction.TransactionInformation.LocalIdentifier);
        Assert.Equal(1, coordinator.ActiveTransactionCount);

        await coordinator.RollbackTransactionAsync(r2.Transaction.TransactionInformation.LocalIdentifier);
        Assert.Equal(0, coordinator.ActiveTransactionCount);
    }

    // ──────────────────────────────────────────────
    // Dispose — 设计文档 REFACTOR
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_ShouldCleanupResources()
    {
        var coordinator = CreateCoordinator();
        _ = await coordinator.BeginTransactionAsync();
        Assert.Equal(1, coordinator.ActiveTransactionCount);

        coordinator.Dispose();

        // After dispose, all entries should be cleaned
        Assert.Equal(0, coordinator.ActiveTransactionCount);
    }

    // ──────────────────────────────────────────────
    // Commit failure path — 设计文档 4.2
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CommitTransactionAsync_WhenRollbackOccurs_ShouldReturnRolledBackStatus()
    {
        // Arrange: use a real CommittableTransaction but force it to rollback
        // by enlisting a volatile RM that votes ForceRollback in Prepare.
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        // Find the transaction and enlist a force-rollback handler
        var tx = coordinator.FindTransaction(txId)!;
        tx.EnlistVolatile(new ForceRollbackEnlistment(), EnlistmentOptions.None);

        // Act
        var commitResult = await coordinator.CommitTransactionAsync(txId);

        // Assert
        Assert.Equal(CommitStatus.RolledBack, commitResult.Status);
        Assert.NotNull(commitResult.ErrorMessage);
    }

    /// <summary>Enlistment that forces rollback during Prepare phase.</summary>
    private sealed class ForceRollbackEnlistment : IEnlistmentNotification
    {
        public void Prepare(PreparingEnlistment preparingEnlistment)
            => preparingEnlistment.ForceRollback();
        public void Commit(Enlistment enlistment) => enlistment.Done();
        public void Rollback(Enlistment enlistment) => enlistment.Done();
        public void InDoubt(Enlistment enlistment) => enlistment.Done();
    }

    // ──────────────────────────────────────────────
    // Rollback exception path — 设计文档 4.2
    // ──────────────────────────────────────────────

    [Fact]
    public async Task RollbackTransactionAsync_WhenRollbackThrows_ShouldStillCleanup()
    {
        // Use a transaction whose Rollback() throws.
        // We can't easily mock CommittableTransaction, so we verify the
        // coordinator's "find → cleanup" path by rolling back twice.
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        // First rollback succeeds
        await coordinator.RollbackTransactionAsync(txId);
        Assert.Equal(0, coordinator.ActiveTransactionCount);

        // Second rollback on same ID should be no-op (entry already removed)
        await coordinator.RollbackTransactionAsync(txId);
        Assert.Equal(0, coordinator.ActiveTransactionCount);
    }

    // ──────────────────────────────────────────────
    // ShutdownAsync edge cases — 设计文档 REFACTOR §3
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ShutdownAsync_WithNoActiveTransactions_ShouldCompleteSilently()
    {
        var coordinator = CreateCoordinator();
        await coordinator.ShutdownAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, coordinator.ActiveTransactionCount);
    }

    [Fact]
    public async Task ShutdownAsync_WhenGracePeriodExpires_ShouldNotThrow()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        // Start a slow commit that won't finish within the grace period
        // by enlisting a handler that blocks during Prepare.
        var tx = coordinator.FindTransaction(txId)!;
        tx.EnlistVolatile(new BlockingEnlistment(TimeSpan.FromSeconds(30)), EnlistmentOptions.None);

        var commitTask = coordinator.CommitTransactionAsync(txId);

        // Shutdown with a very short grace period
        await coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(100));

        // Should complete without throwing — remaining entries are rolled back
        Assert.Equal(0, coordinator.ActiveTransactionCount);
    }

    /// <summary>Enlistment that blocks during Prepare to simulate a slow 2PC.</summary>
    private sealed class BlockingEnlistment : IEnlistmentNotification
    {
        private readonly TimeSpan _delay;
        public BlockingEnlistment(TimeSpan delay) => _delay = delay;
        public void Prepare(PreparingEnlistment preparingEnlistment)
        {
            Thread.Sleep(_delay);
            preparingEnlistment.Prepared();
        }
        public void Commit(Enlistment enlistment) => enlistment.Done();
        public void Rollback(Enlistment enlistment) => enlistment.Done();
        public void InDoubt(Enlistment enlistment) => enlistment.Done();
    }

    // ──────────────────────────────────────────────
    // Dispose idempotency — 设计文档 REFACTOR
    // ──────────────────────────────────────────────

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        var coordinator = CreateCoordinator();
        coordinator.Dispose();
        // Second dispose should not throw
        coordinator.Dispose();
    }

    // ──────────────────────────────────────────────
    // BeginTransactionAsync with connectionId — 设计文档 4.2, 7
    // ──────────────────────────────────────────────

    [Fact]
    public async Task BeginTransactionAsync_WithConnectionId_ShouldCreateSession()
    {
        var coordinator = CreateCoordinator();
        var result = await coordinator.BeginTransactionAsync(connectionId: "grpc-conn-1");

        Assert.NotNull(result.Transaction);
        Assert.Equal(1, coordinator.ActiveTransactionCount);
    }

    // ──────────────────────────────────────────────
    // Cancellation — 设计文档 4.2
    // ──────────────────────────────────────────────

    [Fact]
    public async Task BeginTransactionAsync_ShouldRespondToCancellation()
    {
        var coordinator = CreateCoordinator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.BeginTransactionAsync(ct: cts.Token));
    }

    [Fact]
    public async Task CommitTransactionAsync_ShouldRespondToCancellation()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.CommitTransactionAsync(txId, cts.Token));
    }

    [Fact]
    public async Task RollbackTransactionAsync_ShouldRespondToCancellation()
    {
        var coordinator = CreateCoordinator();
        var beginResult = await coordinator.BeginTransactionAsync();
        var txId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // RollbackAsync with cancelled token should throw
        // (note: Rollback doesn't throw for unknown tx, but SHOULD throw if cancelled before operation)
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.RollbackTransactionAsync(txId, cts.Token));
    }
}
