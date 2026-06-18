namespace Adaptor.Coordinator.Abstractions;

[Flags]
public enum TransactionCapabilities
{
    None = 0,
    /// <summary>Supports standard two-phase commit via IEnlistmentNotification</summary>
    TwoPhaseCommit = 1 << 0,
    /// <summary>Supports promotable single-phase enlistment (PSPE)</summary>
    Promotable = 1 << 1,
    /// <summary>Supports compensating transactions (rollback after failed commit)</summary>
    Compensating = 1 << 2,
}

public static class ResourceManagerExtensions
{
    /// <param name="rm">The resource manager to inspect.</param>
    /// <returns>Combined flags; uses reflection to detect PSPE support dynamically.</returns>
    public static TransactionCapabilities GetTransactionCapabilities(this IResourceManager rm)
    {
        var caps = TransactionCapabilities.None;
        if (rm is ITransactionalResourceManager)
            caps |= TransactionCapabilities.TwoPhaseCommit;

        if (rm.GetType().GetInterfaces().Any(i =>
            i.IsGenericType &&
            i.GetGenericTypeDefinition().FullName?.Contains("IPromotableSinglePhaseNotification") == true))
        {
            caps |= TransactionCapabilities.Promotable;
        }

        return caps;
    }
}
