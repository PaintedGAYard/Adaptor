---
session_start: 2026-06-20T14:00:00+00:00
session_end: TBD
tags: [analysis, middleware, functional-gap, core-business-logic]
revisit:
  - "BlobStream OpCode.Commit - 设计文档中是否应纳入 WebSocket 提交事务路径"
  - "PSPE 优化是否应作为核心功能"
  - "gRPC connection-lifecycle 事件的挂接方式"
files:
  - src/Coordinator/**/*.cs
  - src/Service/**/*.cs
  - src/Driver/Postgre/**/*.cs
  - Design/**/*.md
  - Logs/Work/**/*.md
  - test/**/*.cs
---

# Middleware Functional Analysis — 核心 vs 高级业务逻辑

> time: [20 14:00] · status: resolved · tags: analysis, classification

## 分类方法

根据以下维度区分核心业务逻辑和高级业务逻辑：

| 维度 | 核心 (Core) | 高级 (Advanced) |
|------|------------|----------------|
| 服务化门槛 | 缺少则 Middleware 不可用 | 缺少则可用但体验/健壮性不足 |
| 与设计文档的一致性 | 设计文档明确要求 | 设计文档标注为"未来"或可选项 |
| 测试覆盖率 | 必须被自动化测试覆盖 | 可被手工测试覆盖 |

---

# 第一部分: 核心业务逻辑清单

> time: [20 14:15] · status: resolved · tags: core, inventory

以下为 Middleware "可服务" 所必需的核心功能。

## 1. 事务生命周期管理 (Transaction Lifecycle)

| 功能 | 状态 | 文件/位置 |
|------|:----:|-----------|
| BeginTransaction | ✅ | `TransactionCoordinator.BeginTransactionAsync()` |
| CommitTransaction (2PC) | ⚠️ 见下文 | `TransactionCoordinator.CommitTransactionAsync()` |
| RollbackTransaction | ✅ | `TransactionCoordinator.RollbackTransactionAsync()` |
| GetTransactionStatus | ✅ | `TransactionServiceImpl.GetTransactionStatus()` |
| 事务超时自动回滚 | ✅ | `CommittableTransaction` 内建机制 |
| 会话空闲超时自动回滚 | ✅ | `SessionManager.CleanupExpiredSessions()` |

## 2. 关系数据库操作 (DBRelational)

| 功能 | 状态 | 文件/位置 |
|------|:----:|-----------|
| Execute | ✅ | `RelationalServiceImpl.Execute()`, `PostgreSqlDriver.ExecuteAsync()` |
| Query | ✅ | `RelationalServiceImpl.Query()`, `PostgreSqlDriver.QueryAsync()` |
| ExecuteBatch | ✅ | `RelationalServiceImpl.ExecuteBatch()` |

## 3. 向量搜索 (DBRelationalVector)

| 功能 | 状态 | 文件/位置 |
|------|:----:|-----------|
| Search | ✅ | `RelationalVectorServiceImpl.Search()`, `PgVectorDriver.SearchAsync()` |

## 4. BLOB 操作 (DBBLOB) — Unary

| 功能 | 状态 | 文件/位置 |
|------|:----:|-----------|
| BlobUpload | ✅ | `BlobServiceImpl.BlobUpload()`, `PostgresBlobDriver.UploadAsync()` |
| BlobDownload | ✅ | `BlobServiceImpl.BlobDownload()`, `PostgresBlobDriver.DownloadAsync()` |
| BlobDelete | ✅ 但实现方式存疑 | `BlobServiceImpl.BlobDelete()` — 通过 IRelationalExecuteCapability 执行 DELETE，但 LO unlink 未处理 |
| BlobList | ✅ | `BlobServiceImpl.BlobList()` |

## 5. BlobStream — WebSocket 数据面

| 功能 | 状态 | 文件/位置 |
|------|:----:|-----------|
| 会话协商 (BeginSession/EndSession) | ✅ | `BlobStreamSessionServiceImpl` |
| WebSocket 连接处理 | ✅ | `BlobStreamConnectionHandler.HandleAsync()` |
| Handle 生命周期管理 | ✅ | `HandleManager`, `HandleEntry` |
| 消息协议 (TLV) | ✅ | `BlobStreamMessage`, `OpCode` |
| Open | ✅ | `BlobStreamConnectionHandler.HandleOpenAsync()` |
| Close | ⚠️ 见下文 | `BlobStreamConnectionHandler.HandleCloseAsync()` |
| Read | ✅ | `BlobStreamConnectionHandler.HandleReadAsync()` |
| Write | ⚠️ 见下文 | `BlobStreamConnectionHandler.HandleWriteAsync()` |
| Seek | ⚠️ 见下文 | `BlobStreamConnectionHandler.HandleSeekAsync()` |
| Truncate | ⚠️ 见下文 | `BlobStreamConnectionHandler.HandleTruncateAsync()` |
| Commit (OpCode) | ❓ 未在设计文档中定义 | `BlobStreamConnectionHandler.HandleCommitAsync()` |

## 6. gRPC 协议

| 功能 | 状态 | 文件/位置 |
|------|:----:|-----------|
| 协议定义 (proto) | ✅ | `adaptor.proto` |
| Transaction 服务 | ✅ | `TransactionServiceImpl` |
| DBRelational 服务 | ✅ | `RelationalServiceImpl` |
| DBRelationalVector 服务 | ✅ | `RelationalVectorServiceImpl` |
| DBBLOB 服务 | ✅ | `BlobServiceImpl` |
| BlobStreamControl 服务 | ✅ | `BlobStreamSessionServiceImpl` |

## 7. SK Plugin 层

| 功能 | 状态 | 文件/位置 |
|------|:----:|-----------|
| TransactionPlugin | ✅ | `Coordinator.Plugins.TransactionPlugin` |
| RelationalPlugin | ✅ | `Coordinator.Plugins.RelationalPlugin` |
| VectorSearchPlugin | ✅ | `Coordinator.Plugins.VectorSearchPlugin` |
| Plugin 注册到 Kernel | ❌ `AddAdaptorPluginsToKernel()` 为空 | `ServiceCollectionExtensions.cs` |

---

# 第二部分: 核心业务逻辑中的缺陷

> time: [20 14:30] · status: resolved · tags: defects, core

## 🔴 缺陷 1: CommitTransaction 缺少真正的 2PC 重试 + Partial 状态

**设计文档要求 (DETAILED-DESIGN.md §6.1-6.2):**

```
TransactionCoordinator.Commit() (Phase 1 通过后):
  for each Driver:
    success = false
    for retry in 1..MaxRetryCount:
      try:
        driver.Commit(enlistment)
        success = true
        break
      catch:
        if retry < MaxRetryCount:
          wait(backoff)
          continue
        else:
          → 重试耗尽
    
  全部成功 → COMMITTED
  任一失败 → ROLLED_BACK 或 PARTIAL
```

**当前实现:**

`TransactionCoordinator.CommitCoreAsync()` 直接调用 `entry.Transaction.Commit()`，将 2PC 委托给 .NET `System.Transactions.CommittableTransaction`。此方法没有:
- Per-driver 重试逻辑
- Per-driver 结果追踪
- `COMMIT_STATUS_PARTIAL` 返回路径
- `MaxRetryCount` / `RetryBackoffBase` 的使用

**影响:** 在 .NET 单机环境下（无 MSDTC），`CommittableTransaction` 使用 volatile enlistment 进行内存级 2PC，通常不会失败。但在跨进程 DTC 场景下或有网络分区时，Coordinator 层面的重试和 Partial 状态是恢复机制的关键。当前实现无法处理"部分 Driver 成功"的场景。

**严重性: 🟡 中等** — 在单库场景下不太可能触发；跨数据库异构场景时将成为阻塞问题。

---

## 🔴 缺陷 2: gRPC 连接断开不触发事务自动回滚

**设计文档要求 (§7.3):**

| 条件 | 行为 |
|------|------|
| gRPC 连接断开 | 关联的活跃事务自动 Rollback |

**当前实现:**

- `SessionManager.OnConnectionClosed()` 存在，返回关联的 `SessionContext` 列表
- `TransactionCoordinator` 订阅了 `OnSessionTimeout` 事件
- **但** gRPC 层（`AdaptorService.cs` 或 `Program.cs`）从未调用 `SessionManager.OnConnectionClosed()`

`Program.cs` 中没有注册任何连接关闭回调。gRPC 的 `ServerCallContext` 提供了 `CancellationToken`（`context.CancellationToken`），但该 token 仅在单个 RPC 调用期间有效，不是连接级生命周期事件。

**影响:** Consumer gRPC 连接崩溃或断开时，活跃事务继续存在直到超时（默认 30s），而不是立即回滚。

**严重性: 🟡 中等**

---

## 🔴 缺陷 3: ShutdownAsync 未集成到应用生命周期

**设计文档要求 (§7.3):**

| 条件 | 行为 |
|------|------|
| 服务关闭（Graceful） | 等待进行中的 Commit 完成，Rollback 其余事务 |

**当前实现:**

- `TransactionCoordinator.ShutdownAsync()` 已实现，等待 pending commits 完成，然后回滚所有活跃事务
- **但** `Program.cs` 中没有调用它

标准的 .NET `WebApplication.Lifetime.ApplicationStopping` 事件可用于注册关闭回调，但当前代码没有挂接。

**影响:** 进程退出时正在提交的事务可能被强制中断，导致不一致。

**严重性: 🟡 中等**

---

## 🔴 缺陷 4: BlobDelete 未处理底层 LO 清理

**设计文档要求:** BLOB 删除时应清理 PostgreSQL Large Object 和映射表中的记录。

**当前实现:**

`BlobServiceImpl.BlobDelete()` 仅执行:
```sql
DELETE FROM adaptor_blob_store WHERE key = @key
```
但**没有调用** `lo_unlink(oid)` 来释放 Large Object 占用的磁盘空间。

此外，`PostgresBlobDriver` 没有实现 `IBlobDeleteCapability` 接口（该接口不存在于抽象层中）。当前实现绕过了 Driver 层，直接在 gRPC 服务中通过 `IRelationalExecuteCapability` 执行 SQL。

**影响:** 调用 BlobDelete 后，映射表中的记录被删除但 LO 数据保留，造成存储泄漏。

**严重性: 🟢 较低** — 但长期运行会累积存储碎片。

---

## 🔴 缺陷 5: BlobStream 随机访问操作位置状态不一致

**设计文档要求 (§6.1):**

```
OpenAsync(key, mode, tx) → (fd, size)
ReadAsync(fd, count, tx) → data (从当前位置读取)
WriteAsync(fd, data, tx) → bytes_written (从当前位置写入)
SeekAsync(fd, offset, origin, tx) → new_position
CloseAsync(fd, tx)
TruncateAsync(fd, length, tx)
```

**当前实现:**

根据 [2026-06-20 测试工作记录] 中的发现，`PostgresBlobDriver` 的随机访问路径使用 `lo_get(oid, offset, len)` / `lo_put(oid, offset, data)` API，这些 API **不维护隐式位置游标**。因此:

| 操作 | 当前行为 | 设计行为 |
|------|----------|---------|
| `CloseAsync` | ✅ 实现 | 无差异 |
| `ReadAsync` | ❌ 从 offset 0 读取，不是从 seek 位置 | 从当前 seek 位置读取 |
| `WriteAsync` | ❌ 在 offset 0 写入，不是从 seek 位置 | 从当前 seek 位置写入 |
| `SeekAsync` | ✅ 实现 (但影响不到 Read/Write) | 应影响后续 Read/Write 位置 |
| `TruncateAsync` | ✅ 实现 | 无差异 |

`PostgresBlobDriver` 中有 `_randomAccessStates` dict 但似乎未被 Read/Write 正确使用。

**影响:** BlobStream 协议的 Seek/Read/Write 语义被破坏。数据平面功能不可靠。

**严重性: 🔴 高** — 直接导致 4 个测试被跳过。

---

## 🟡 缺陷 6: appsettings.json 缺少 BlobStream 超时配置

**当前配置 (appsettings.json):**
```json
"Adaptor": {
    "DefaultTransactionTimeout": "00:00:30",
    "SessionIdleTimeout": "00:01:00",
    "MaxRetryCount": 3,
    "RetryBackoffBase": "00:00:00.100",
    "MaxTransactionTimeout": "00:05:00",
    "SessionCleanupInterval": "00:00:30"
}
```

**缺少的字段:** `BlobStreamTransactionTimeout` 和 `MaxBlobStreamTransactionTimeout`

这些字段在 `CoordinatorOptions` 中有定义和默认值（24h / 72h），但未包含在配置模板中，部署时管理员无法调整。

**严重性: 🟢 较低** — 默认值合理，配置缺失不影响运行。

---

## 🟡 缺陷 7: BlobStream OpCode.Commit — 协议设计争议

`BlobStreamConnectionHandler` 实现了 `HandleCommitAsync` 方法，响应 `OpCode.Commit`。这允许 WebSocket 通道直接提交事务。

**问题:**
- 设计文档（BLOB-STREAM-DESIGN.md）中没有定义此 OpCode
- 设计原则是 "Control/Data Plane 分离" — gRPC 控制面负责事务生命周期
- WebSocket 提交事务绕过了 gRPC 控制面

**影响:** 这不是功能缺陷，但可能破坏架构一致性。如果允许 WebSocket 提交事务，则相应的设计文档应更新。

**严重性: 🟢 设计讨论**

---

# 第三部分: 高级业务逻辑（非服务化门槛）

> time: [20 14:45] · status: resolved · tags: advanced, future

以下功能属于"有更好，没有也能用"的范畴。

## 1. 非 PostgreSQL Driver 实现

| Driver | 状态 | 说明 |
|--------|:----:|------|
| MySQL Driver | ❌ | 无实现 |
| Milvus Driver | ❌ | 无实现 |
| AWS S3 Driver | ❌ | 无实现 |
| SQLite-Vector Driver | ❌ | 无实现 |

项目当前仅实现了 PostgreSQL (pgvector) 驱动。其他存储系统是设计文档中规划的，但不影响当前的服务化。

## 2. 多 Driver 异构场景

所有集成测试均针对 PG17 运行。没有跨异构存储的 2PC 测试（如 MySQL + pgvector + S3）。

## 3. PSPE 优化

设计文档提到三个 PG Driver 共享连接时可使用 Promotable Single Phase Enlistment (PSPE) 优化，使事务在 PG 本地完成，无需提升为分布式。当前实现使用 `EnlistVolatile`。

## 4. AddAdaptorPluginsToKernel

当前空实现。SK Plugin 被注册为 singleton 服务，但没有通过 `Kernel.Plugins.AddFromObject` 加载到 SK Kernel。

## 5. 持久化事务状态

所有事务状态均存储在内存中。进程重启后事务丢失。设计文档标注为"未来版本"。

## 6. 负载均衡 / Sticky Session

`HandleManager` 是进程内状态，无法跨实例共享。需要 sticky session 将 WebSocket 连接路由到同一实例。

## 7. 更多协议特性

- `TransactionContext.ambient` 事务支持（proto 注释中标注为未来）
- SetTransactionTimeout RPC（设计文档标注已移除）

---

# 第四部分: 测试覆盖分析

> time: [20 15:00] · status: resolved · tags: testing, coverage

## 当前测试基线: 324 passed, 0 skipped

| 测试项目 | 数量 | 覆盖内容 |
|----------|:----:|---------|
| Driver.Postgre | ~87 | 单元测试 + Testcontainers 集成测试 |
| Coordinator | ~124 | Coordinator / SessionManager / Plugins / Models |
| Service | ~29 | 转换帮助方法 + gRPC service mock 测试 |
| Integration | ~2 | 完整多 Driver 2PC 端到端测试 |

## 测试缺口

| 缺口 | 说明 | 优先级 |
|------|------|:------:|
| **BlobStream WebSocket 端到端测试** | 没有测试启动 WebSocket 连接、发送 Open/Read/Write/Close 消息、验证响应 | 🔴 高 |
| **并发场景测试** | 多事务并发、单事务内并发操作、多 handle 并发 | 🟡 中 |
| **超时/边界条件** | 事务超时自动回滚、会话空闲超时连接清理、大文件上传 | 🟡 中 |
| **SessionManager.OnConnectionClosed** | 无测试验证 gRPC 连接关闭后事务被回滚 | 🟡 中 |
| **ShutdownAsync** | 无测试验证优雅关闭行为 | 🟢 低 |
| **BlobDelete LO 清理** | 无测试验证 delete 后 LO 被 unlink | 🟢 低 |
| **SK Plugin Kernel 集成** | 无测试验证 Plugin 通过 Kernel 调用 | 🟢 低 |

---

# 第五部分: 总结与服务化就绪度评估

> time: [20 15:15] · status: resolved · tags: summary, readiness

## 核心功能就绪度

| 功能领域 | 就绪度 | 关键阻塞项 |
|----------|:------:|-----------|
| 事务生命周期 | 🟡 90% | 2PC 重试/Partial 状态缺失 |
| DBRelational | ✅ 100% | — |
| DBRelationalVector | ✅ 100% | — |
| DBBLOB (Unary) | 🟡 85% | BlobDelete LO 泄漏 |
| BlobStream (WebSocket) | 🔴 60% | Seek/Read/Write 位置状态破坏 |
| gRPC 协议 | ✅ 100% | — |
| SK Plugin | 🟡 70% | Plugin 未注册到 Kernel |

## 服务化前必须修复的缺陷 (P0)

1. **🔴 BlobStream 随机访问位置状态** — Seek/Read/Write 语义被破坏，数据平面不可用
2. **🟡 2PC 重试 + Partial 状态** — 单库场景不影响，但异构场景不可用
3. **🟡 gRPC 连接断开自动回滚** — 需要将 OnConnectionClosed 挂接到 gRPC 连接事件

## 服务化前建议修复的缺陷 (P1)

4. **🟡 ShutdownAsync 集成** — 注册到 `ApplicationStopping` 事件
5. **🟢 BlobDelete LO 清理** — 补充 `lo_unlink` 调用
6. **🟢 appsettings.json 配置补充** — 添加 BlobStream 超时配置

## 服务化就绪度总评

**当前状态: 🟡 75%** — 核心事务生命周期和基础数据操作已就绪，但 BlobStream 数据平面的随机访问存在实现缺陷，2PC 重试和连接生命周期管理有待完善。
