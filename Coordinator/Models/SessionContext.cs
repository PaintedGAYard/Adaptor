namespace Adaptor.Coordinator.Models;

/// <summary>
/// Session context bound to an active transaction.
/// </summary>
public sealed class SessionContext
{
    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    /// The associated transaction's <see cref="System.Transactions.TransactionInformation.LocalIdentifier"/>.
    /// The coordinator uses this to locate the <see cref="System.Transactions.Transaction"/> instance.
    /// </summary>
    public string TransactionLocalIdentifier { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public bool IsClosed { get; set; }

    /// <summary>Update <see cref="LastActivityAt"/> to now.</summary>
    public void Touch() => LastActivityAt = DateTime.UtcNow;
}
