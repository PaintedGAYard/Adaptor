namespace Adaptor.Service.BlobStream;

/// <summary>
/// Tracks the state of an opened BLOB handle on the server side.
/// Each entry maps a client-visible handle ID to a PostgreSQL Large Object file descriptor.
/// </summary>
internal sealed class HandleEntry : IDisposable
{
    /// <summary>Client-visible handle identifier.</summary>
    public long HandleId { get; init; }

    /// <summary>PostgreSQL Large Object file descriptor.</summary>
    public int LoFd { get; init; }

    /// <summary>BLOB key associated with this handle.</summary>
    public string Key { get; init; } = "";

    /// <summary>The .NET transaction LocalIdentifier this handle belongs to.</summary>
    public string TransactionId { get; init; } = "";

    /// <summary>The gRPC connection ID that owns this handle.</summary>
    public string ConnectionId { get; init; } = "";

    /// <summary>Last activity timestamp for idle GC.</summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>Per-handle serialisation gate ensuring atomic Seek+Read/Write.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose()
    {
        Gate.Dispose();
    }
}
