// TransactionIdRegistry 已移除。
//
// gRPC transaction_id 直接复用 TransactionInformation.LocalIdentifier，
// 不再需要自定义 ID 映射。通过 TransactionCoordinator.FindTransactionInfo(localId)
// 反向查找。本文件保留为注释以避免误引用。

