namespace Adaptor.Test.BlobStream;

/// <summary>
/// Tests for <see cref="HandleEntry"/>.
/// Design-based: derived from BLOB-STREAM-DESIGN.md §4.3.
/// </summary>
public sealed class HandleEntryTest
{
    // ──────────────────────────────────────────────
    // HandleEntry structure — BLOB-STREAM §4.3
    // ──────────────────────────────────────────────

    [Fact]
    public void HandleEntry_ShouldStoreAllFields()
    {
        var entry = new HandleEntry
        {
            HandleId = 12345,
            LoFd = 7,
            Key = "my-blob.bin",
            TransactionId = "tx-abc-123",
            ConnectionId = "conn-xyz-789",
        };

        Assert.Equal(12345, entry.HandleId);
        Assert.Equal(7, entry.LoFd);
        Assert.Equal("my-blob.bin", entry.Key);
        Assert.Equal("tx-abc-123", entry.TransactionId);
        Assert.Equal("conn-xyz-789", entry.ConnectionId);
    }

    [Fact]
    public void HandleEntry_ShouldSetLastActivityAtOnCreation()
    {
        var before = DateTime.UtcNow;
        var entry = new HandleEntry();
        var after = DateTime.UtcNow;

        Assert.InRange(entry.LastActivityAt, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public void HandleEntry_ShouldUpdateLastActivityAt()
    {
        var entry = new HandleEntry();
        var before = entry.LastActivityAt;

        Thread.Sleep(5);
        entry.LastActivityAt = DateTime.UtcNow;

        Assert.True(entry.LastActivityAt > before);
    }

    // ──────────────────────────────────────────────
    // Per-handle Gate — BLOB-STREAM §4.3, §4.4
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HandleEntry_ShouldProvidePerHandleGate()
    {
        var entry = new HandleEntry();

        // Gate should be initially available
        Assert.True(await entry.Gate.WaitAsync(0));
        entry.Gate.Release();
    }

    [Fact]
    public async Task HandleEntry_Gate_ShouldSerializeAccess()
    {
        var entry = new HandleEntry();
        var taskStarted = new TaskCompletionSource();

        var task1 = Task.Run(async () =>
        {
            await entry.Gate.WaitAsync();
            taskStarted.TrySetResult();
            // Hold the gate briefly
            await Task.Delay(100);
            entry.Gate.Release();
        });

        // Wait for task1 to enter the gate
        await taskStarted.Task;

        // task2 should not be able to enter while task1 holds the gate
        var couldEnter = await entry.Gate.WaitAsync(10);
        Assert.False(couldEnter);

        await task1;

        // After release, task2 can enter
        Assert.True(await entry.Gate.WaitAsync(0));
        entry.Gate.Release();
    }

    [Fact]
    public void HandleEntry_Gate_ShouldBeInitiallyAvailable()
    {
        var entry = new HandleEntry();
        Assert.Equal(1, entry.Gate.CurrentCount);
    }

    // ──────────────────────────────────────────────
    // Dispose — BLOB-STREAM §4.3
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HandleEntry_Dispose_ShouldDisposeGate()
    {
        var entry = new HandleEntry();
        entry.Dispose();

        // After dispose, WaitAsync should throw ObjectDisposedException
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            entry.Gate.WaitAsync());
    }

    [Fact]
    public void HandleEntry_Dispose_ShouldBeIdempotent()
    {
        var entry = new HandleEntry();
        entry.Dispose();
        // Second dispose should not throw
        entry.Dispose();
    }
}
