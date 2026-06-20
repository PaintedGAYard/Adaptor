# Adaptor — 代码审查与重构会议纪要

> **日期**: 2026-06-18  
> **主题**: 对初始实现的代码审查与架构重构讨论  
> **状态**: 最终稿

---

## 议程

1. 审查 `TransactionContext` 的必要性
2. 统一三个子系统间的 Transaction ID 设计
3. 简化 Coordinator 状态管理
4. 确定并发保护策略
5. 决定 gRPC 协议变更

---

## 讨论详情

### 1. 删除 TransactionContext

**问题**: `TransactionContext` 封装了 `CommittableTransaction`、状态追踪、信号量、超时监控等额外信息，是否有必要？

**决策**: **删除 `TransactionContext`**。

理由：
- `System.Transactions.Transaction` 已包含全部需要的信息（通过 `TransactionInformation`）
- `SemaphoreSlim Gate` 不必要：DTC 状态机本身线程安全；数据操作并发保护应下放到 Driver 层
- `MonitorTransactionTimeoutAsync` 冗余：`CommittableTransaction` 构造时传入 `TimeSpan` 超时，.NET 内部自动处理超时回滚
- 替代方案：`ConcurrentDictionary<string, TransactionEntry>`，其中 `TransactionEntry` 仅含 `CommittableTransaction` 和 `OriginalTimeout`

**影响**：
- `TransactionContext.cs` 文件删除
- 自定义 `TransactionState` 枚举删除（仅保留 `CommitStatus`）
- `TransactionStateExtensions.IsTerminal()` 删除
- `BeginTransactionResult` 改为返回 `Transaction` 基类
- `SessionContext.TransactionInfo` 改为 `TransactionLocalIdentifier`（string）

### 2. Transaction ID 设计统一

**问题**: Coordinator、Session、gRPC 层对事务标识的使用方式不一致。

**决策**:

| 原则 | 说明 |
|------|------|
| Coordinator 内部使用 `CommittableTransaction` | 已实现 |
| 对外暴露 `Transaction` 基类 | `BeginTransactionResult` 返回 `Transaction` |
| 进程内标识 | `TransactionInformation.LocalIdentifier` |
| MSDTC 跨进程标识 | `TransactionInformation.DistributedIdentifier` |
| 所有公共方法接受 string transactionId | 替代之前的 `TransactionInformation` 参数 |
| 通过 Identifier 检索实例 | `FindTransaction(string)` → `Transaction?` |

**影响**：
- `TransactionCoordinator` 所有公共方法改为接受 `string transactionId`
- `AdaptorServiceContext.ResolveTx` 删除
- `TransactionIdRegistry.cs` 确认不再需要（已是空注释文件）

### 3. 状态简化与 Pending Commit Task

**问题**: `TransactionState` 自定义枚举有 9 个值，绝大部分是内部实现细节。

**决策**: 精简状态管理：

- 删除自定义 `TransactionState` 枚举（`Preparing` / `Prepared` / `Committing` / `RollingBack` / `Timeout` 等）
- 外部查询直接映射 `System.Transactions.TransactionStatus`（4 值）
- 用 **Pending Commit Task 表** 替代状态机保护 `ShutdownAsync`：

```
_pendingCommits: ConcurrentDictionary<string, Task<CommitResult>>

ShutdownAsync:
  1. await Task.WhenAll(_pendingCommits.Values)  // 等进行中的提交
  2. 回滚其余活跃事务
```

**影响**：
- gRPC `GetTransactionStatusResponse.state` 简化
- `gRPC TransactionState` 枚举从 9 值减为 5 值（含 UNSPECIFIED）
- `GetTransactionStatus` 中的 `enlisted_drivers` 字段移除

### 4. 超时管理

**问题**: `SetTransactionTimeout` RPC 在 `CommittableTransaction` 构造后无法实际修改超时。

**决策**:
- **删除 `SetTransactionTimeout` RPC**
- `ExpiresAt` 仅在 `BeginTransaction` 时一次性计算，后续不维护
- 如需长时间操作，Consumer 应创建超时无限的独立事务
- 超时完全由 `CommittableTransaction` 内建机制处理

### 5. 并发保护下放到 Driver 层

**问题**: Coordinator 的 `SemaphoreSlim Gate` 保护同一事务内的操作顺序执行。

**决策**: **并发保护由各 Driver 自行负责**。
- Coordinator 不做串行化
- Npgsql 等有状态驱动：Driver 内部使用 per-transaction `SemaphoreSlim`
- S3 等无状态驱动：天然支持并发，无需锁
- 这符合"Driver 负责事务"的设计原则

### 6. gRPC 协议变更

| 变更 | 说明 |
|------|------|
| 删除 `SetTransactionTimeout` RPC | 从 service 和消息定义中移除 |
| 简化 `TransactionState` 枚举 | 仅保留 UNSPECIFIED / ACTIVE / COMMITTED / ROLLED_BACK / IN_DOUBT |
| 简化 `GetTransactionStatusResponse` | 移除 `enlisted_drivers` 字段 |

---

## 关键概念更新

| 概念 | 更新后定义 |
|------|-----------|
| Transaction | .NET `System.Transactions.Transaction` 实例，中间件内部使用 `CommittableTransaction` |
| Resource Manager (RM) | 每个 Driver 是一个 RM，通过 `IEnlistmentNotification` 参与两阶段提交 |
| Enlistment | Driver 将自身注册到 .NET Transaction 的过程 |
| Coordinator 状态 | 仅 `ConcurrentDictionary<string, TransactionEntry>` + `_pendingCommits` 表 |
| 并发保护 | Driver 层自行负责，Coordinator 不做串行化 |
| 超时 | 仅 `BeginTransaction` 时指定，不可修改 |

---

## 额外决定

**`RetryPolicy` 删除**：2PC 重试语义由 .NET `System.Transactions` 内部管理，Driver 的 `IEnlistmentNotification.Commit()` 内部可自行重试，Coordinator 层不需要追踪复杂事务状态。`RetryPolicy.cs` 已删除。

## 实施检查清单

- [x] 删除 `TransactionContext.cs`
- [x] 精简 `TransactionState.cs`（仅保留 `CommitStatus`）
- [x] 重构 `TransactionCoordinator.cs`
  - [x] 替换 `_activeTransactions` 为 `_entries`（`TransactionEntry`）
  - [x] 移除 Gate / MonitorTransactionTimeoutAsync / SetTransactionTimeout
  - [x] 移除 GetTransactionState / GetTransactionExpiry / GetEnlistedDrivers
  - [x] 添加 _pendingCommits 表
  - [x] 所有公共方法改为 string transactionId
  - [x] 添加 FindTransaction(string)
- [x] 更新 `BeginTransactionResult`（返回 `Transaction`）
- [x] 更新 `SessionContext`（`TransactionInfo` → `TransactionLocalIdentifier`）
- [x] 更新 `SessionManager`
- [x] 更新 `AdaptorService.cs`
- [x] 更新 `adaptor.proto`
- [x] 更新 `DETAILED-DESIGN.md`
- [x] 删除 `RetryPolicy.cs`（死代码）
- [ ] Driver 添加 per-transaction 并发锁

---

*会议纪要结束*
