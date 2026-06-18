// TransactionIdRegistry has been removed.
//
// gRPC transaction_id now directly reuses TransactionInformation.LocalIdentifier,
// eliminating the need for custom ID mapping. Use TransactionCoordinator.FindTransactionInfo(localId)
// for reverse lookups. This file is preserved as a comment to prevent accidental references.

