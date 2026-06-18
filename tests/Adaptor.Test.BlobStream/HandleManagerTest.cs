namespace Adaptor.Test.BlobStream;

/// <summary>
/// Tests for <see cref="HandleManager"/>.
/// Design-based: derived from BLOB-STREAM-DESIGN.md §4.2–4.3, §9.2.
/// </summary>
public sealed class HandleManagerTest
{
    private const string ConnectionId = "test-conn-1";
    private const string TransactionId = "test-tx-1";

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static HandleManager CreateManager()
    {
        return new HandleManager(Substitute.For<ILogger<HandleManager>>());
    }

    // ──────────────────────────────────────────────
    // Register — BLOB-STREAM §4.2
    // ──────────────────────────────────────────────

    [Fact]
    public void Register_ShouldAllocateUniqueHandleIds()
    {
        var mgr = CreateManager();

        var h1 = mgr.Register(ConnectionId, 1, "key1", TransactionId);
        var h2 = mgr.Register(ConnectionId, 2, "key2", TransactionId);

        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Register_ShouldTrackActiveHandleCount()
    {
        var mgr = CreateManager();

        Assert.Equal(0, mgr.ActiveHandleCount);

        mgr.Register(ConnectionId, 1, "key1", TransactionId);
        Assert.Equal(1, mgr.ActiveHandleCount);

        mgr.Register(ConnectionId, 2, "key2", TransactionId);
        Assert.Equal(2, mgr.ActiveHandleCount);
    }

    [Fact]
    public void Register_ShouldStoreAllProvidedFields()
    {
        var mgr = CreateManager();
        const int loFd = 42;
        const string key = "my-blob.dat";

        var handle = mgr.Register(ConnectionId, loFd, key, TransactionId);

        Assert.True(mgr.TryGet(handle, out var entry));
        Assert.Equal(ConnectionId, entry.ConnectionId);
        Assert.Equal(loFd, entry.LoFd);
        Assert.Equal(key, entry.Key);
        Assert.Equal(TransactionId, entry.TransactionId);
        Assert.Equal(handle, entry.HandleId);
    }

    [Fact]
    public void Register_ShouldRejectExceedingPerConnectionLimit()
    {
        var mgr = CreateManager();

        // Register 64 handles (the max) should succeed
        for (int i = 0; i < 64; i++)
        {
            mgr.Register(ConnectionId, i, $"key{i}", TransactionId);
        }

        // The 65th should throw
        var ex = Assert.Throws<InvalidOperationException>(() =>
            mgr.Register(ConnectionId, 65, "key-extra", TransactionId));

        Assert.Contains("limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_ShouldAllowDifferentConnectionsToReachLimitIndependently()
    {
        var mgr = CreateManager();

        for (int i = 0; i < 64; i++)
        {
            mgr.Register("conn-A", i, $"key{i}", TransactionId);
        }

        // Another connection should still be able to register
        var handle = mgr.Register("conn-B", 0, "key-other", TransactionId);
        Assert.True(handle > 0);
    }

    // ──────────────────────────────────────────────
    // TryGet — BLOB-STREAM §4.2
    // ──────────────────────────────────────────────

    [Fact]
    public void TryGet_ShouldReturnEntryForValidHandle()
    {
        var mgr = CreateManager();
        var handle = mgr.Register(ConnectionId, 1, "key", TransactionId);

        Assert.True(mgr.TryGet(handle, out var entry));
        Assert.NotNull(entry);
    }

    [Fact]
    public void TryGet_ShouldReturnFalseForUnknownHandle()
    {
        var mgr = CreateManager();
        Assert.False(mgr.TryGet(999999, out _));
    }

    [Fact]
    public void TryGet_ShouldUpdateLastActivityAt()
    {
        var mgr = CreateManager();
        var handle = mgr.Register(ConnectionId, 1, "key", TransactionId);

        Assert.True(mgr.TryGet(handle, out var entry));
        var before = entry.LastActivityAt;

        Thread.Sleep(5);
        Assert.True(mgr.TryGet(handle, out var _));

        Assert.True(entry.LastActivityAt > before);
    }

    // ──────────────────────────────────────────────
    // Remove — BLOB-STREAM §4.2
    // ──────────────────────────────────────────────

    [Fact]
    public void Remove_ShouldRemoveHandle()
    {
        var mgr = CreateManager();
        var handle = mgr.Register(ConnectionId, 1, "key", TransactionId);

        var removed = mgr.Remove(handle, out var entry);

        Assert.True(removed);
        Assert.NotNull(entry);
        Assert.False(mgr.TryGet(handle, out _));
    }

    [Fact]
    public void Remove_ShouldDecrementActiveCount()
    {
        var mgr = CreateManager();
        var handle = mgr.Register(ConnectionId, 1, "key", TransactionId);

        Assert.Equal(1, mgr.ActiveHandleCount);
        mgr.Remove(handle, out _);
        Assert.Equal(0, mgr.ActiveHandleCount);
    }

    [Fact]
    public void Remove_ForUnknownHandle_ShouldReturnFalse()
    {
        var mgr = CreateManager();
        Assert.False(mgr.Remove(999, out _));
    }

    // ──────────────────────────────────────────────
    // InvalidateHandlesForTransaction — BLOB-STREAM §9.1 (#4)
    // ──────────────────────────────────────────────

    [Fact]
    public void InvalidateHandlesForTransaction_ShouldRemoveAllForTx()
    {
        var mgr = CreateManager();

        var h1 = mgr.Register(ConnectionId, 1, "k1", "tx-1");
        var h2 = mgr.Register(ConnectionId, 2, "k2", "tx-1");
        mgr.Register(ConnectionId, 3, "k3", "tx-2"); // different tx

        var removed = mgr.InvalidateHandlesForTransaction("tx-1");

        Assert.Equal(2, removed.Count);
        Assert.Contains(removed, e => e.HandleId == h1);
        Assert.Contains(removed, e => e.HandleId == h2);

        // tx-1 handles should be gone
        Assert.False(mgr.TryGet(h1, out _));
        Assert.False(mgr.TryGet(h2, out _));
    }

    [Fact]
    public void InvalidateHandlesForTransaction_ShouldNotAffectOtherTransactions()
    {
        var mgr = CreateManager();

        var h1 = mgr.Register(ConnectionId, 1, "k1", "tx-1");
        var h2 = mgr.Register(ConnectionId, 2, "k2", "tx-2");

        mgr.InvalidateHandlesForTransaction("tx-1");

        // tx-2 handle should still exist
        Assert.True(mgr.TryGet(h2, out _));
    }

    // ──────────────────────────────────────────────
    // RemoveAllForConnection — BLOB-STREAM §4.4
    // ──────────────────────────────────────────────

    [Fact]
    public void RemoveAllForConnection_ShouldRemoveAllHandlesForConnection()
    {
        var mgr = CreateManager();

        var h1 = mgr.Register("conn-A", 1, "k1", TransactionId);
        var h2 = mgr.Register("conn-A", 2, "k2", TransactionId);
        mgr.Register("conn-B", 3, "k3", TransactionId); // different conn

        var removed = mgr.RemoveAllForConnection("conn-A");

        Assert.Equal(2, removed.Count);
        Assert.Contains(removed, e => e.HandleId == h1);
        Assert.Contains(removed, e => e.HandleId == h2);

        Assert.False(mgr.TryGet(h1, out _));
        Assert.False(mgr.TryGet(h2, out _));
    }

    [Fact]
    public void RemoveAllForConnection_ShouldNotAffectOtherConnections()
    {
        var mgr = CreateManager();

        var h1 = mgr.Register("conn-A", 1, "k1", TransactionId);
        var h2 = mgr.Register("conn-B", 2, "k2", TransactionId);

        mgr.RemoveAllForConnection("conn-A");

        Assert.True(mgr.TryGet(h2, out _));
    }

    // ──────────────────────────────────────────────
    // Cleanup — BLOB-STREAM §4.2, §9.2 (#10)
    // ──────────────────────────────────────────────

    [Fact]
    public void Dispose_ShouldCleanupAllHandles()
    {
        var mgr = CreateManager();
        mgr.Register(ConnectionId, 1, "k1", TransactionId);
        mgr.Register(ConnectionId, 2, "k2", TransactionId);

        Assert.Equal(2, mgr.ActiveHandleCount);

        mgr.Dispose();

        Assert.Equal(0, mgr.ActiveHandleCount);
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        var mgr = CreateManager();
        mgr.Dispose();
        mgr.Dispose(); // Should not throw
    }

    // ──────────────────────────────────────────────
    // Design: Handle IDs start from random — BLOB-STREAM §4.2
    // ──────────────────────────────────────────────

    [Fact]
    public void HandleIds_ShouldBeLargePositiveNumbers()
    {
        var mgr = CreateManager();

        var handle = mgr.Register(ConnectionId, 1, "key", TransactionId);

        // Handle should be a large positive number (starts from random << 32)
        Assert.True(handle > 0);
        // Should be much larger than simple sequential IDs
        Assert.True(handle > 1_000_000_000_000, "Handle should start from a large random value");
    }
}
