namespace Adaptor.Coordinator.Abstractions;

public enum ResourceType
{
    Sql,
    Vector,
    Blob,
}

/// <summary>
/// Base interface for all storage drivers.
/// </summary>
/// <remarks>All drivers must implement this interface.</remarks>
public interface IResourceManager
{
    /// <summary>A human-readable name for the driver instance.</summary>
    string Name { get; }

    /// <summary>The type of storage resource this driver manages.</summary>
    ResourceType ResourceType { get; }

    /// <summary>
    /// Unique identifier (following ADO convention).
    /// Used to identify this RM in .NET System.Transactions.
    /// </summary>
    Guid ResourceManagerIdentifier { get; }
}
