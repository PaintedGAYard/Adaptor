using System.Transactions;

namespace Adaptor.Coordinator.Abstractions;

/// <summary>
/// Opt-in interface for drivers that participate in distributed transaction coordination.
/// </summary>
/// <remarks>Only drivers implementing this interface are enlisted in transactions.</remarks>
public interface ITransactionalResourceManager : IResourceManager
{
    /// <summary>Enlist this driver in the specified .NET <see cref="Transaction"/>.</summary>
    /// <param name="transaction">The transaction to enlist in. Must be active.</param>
    /// <remarks>
    /// Implementations should call <see cref="Transaction.EnlistVolatile"/> or
    /// <see cref="Transaction.EnlistDurable"/> to complete registration.
    /// </remarks>
    void Enlist(Transaction transaction);
}
