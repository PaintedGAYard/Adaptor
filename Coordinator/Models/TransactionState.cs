namespace Adaptor.Coordinator.Models;

public enum CommitStatus
{
    Committed = 1,
    /// <summary>Some drivers committed, some failed after retries exhausted</summary>
    Partial = 2,
    RolledBack = 3,
    /// <summary>Transaction timed out (distinct from <see cref="RolledBack"/>)</summary>
    Timeout = 4,
}
