# TODO — 2026-06-20 Core Business Logic Completion

> 按 Phase 顺序推进，每次只 focus 一个 task
> 详细背景参见 [Plan.md](./Plan.md)

---

## Phase A: 设计对齐 — 移除 OpCode.Commit ✅

### A.1 OpCode 定义更新
- [x] 更新 `OpCodes_ShouldMatchDesign` 测试：移除 `OpCode.Commit` 断言
- [x] 从 `OpCode.cs` 删除 `Commit = 0x07` 常量

### A.2 ConnectionHandler 清理
- [x] 从 `BlobStreamConnectionHandler.cs` 删除 `HandleCommitAsync` 方法
- [x] 从 `ProcessRequestAsync` switch 中移除 `OpCode.Commit` 分支
- [x] 更新 `BlobStreamConnectionHandlerTest.cs` 中与 Commit 相关的测试/删除（无变化）

### A.3 验证
- [x] 运行 BlobStream 测试确认全部通过（80/0）
- [x] 构建 0 错误 0 警告

---

## Phase B-1: BlobDelete LO Cleanup ✅

### B1.1 实现 lo_unlink
- [x] 修改 `BlobDelete`：查询 OID → `lo_unlink(oid)` → `DELETE FROM adaptor_blob_store`
- [x] 分三步执行，均在事务内完成（参与 2PC）

### B1.2 验证
- [x] 构建通过，所有测试通过

---

## Phase B-2: ShutdownAsync 集成 ✅

### B2.1 Program.cs 生命周期注册
- [x] 在 `Program.cs` 中添加 `app.Lifetime.ApplicationStopping` 回调
- [x] 回调中注入 `TransactionCoordinator` 并调用 `ShutdownAsync`（5s grace period）

### B2.2 验证
- [x] 构建通过，所有测试通过

---

## Phase B-3: 配置更新 ✅

### B3.1 CoordinatorOptions 扩展
- [x] 添加 `PausedTransactionTimeout` 属性（默认 5 分钟）

### B3.2 appsettings.json 清理
- [x] 删除 `MaxRetryCount` 和 `RetryBackoffBase` 配置项
- [x] 添加 `BlobStreamTransactionTimeout` 配置（24h）
- [x] 添加 `MaxBlobStreamTransactionTimeout` 配置（72h）
- [x] 添加 `PausedTransactionTimeout` 配置（5min）

---

## Phase C-1: Session Store Transaction Association ✅

### C1.1 BlobStreamSessionEntry 扩展
- [x] 添加 `TransactionId` 字段
- [x] 添加 `PauseStartedAt` 字段
- [x] 添加 `ReconnectCount` 字段

### C1.2 BlobStreamSessionStore 扩展
- [x] 添加 `TryReconnect(token, connectionId)` 方法（检查 session 存在性、暂停超时、更新连接状态）
- [x] 添加 `MarkPaused(token)` 方法
- [x] 修改 `CleanupExpiredSessions` 检查 `PauseStartedAt` 超时
- [x] 支持 `PausedTransactionTimeout` 构造参数

### C1.3 测试
- [x] 现有 80 个 BlobStream 测试全部通过，无回归

---

## Phase C-2: ConnectionHandler Lifecycle Refactor ✅

### C2.1 HandleAsync 重构
- [x] 拆分 `HandleAsync` 为首次连接路径和重连路径
- [x] WS 非优雅断开 → pause（保留事务，清理 handles，`MarkPaused`）
- [x] WS 重连 → 验证 session 和事务状态，恢复连接，递增 ReconnectCount
- [x] WS 正常关闭/EndSession → Terminate（回滚事务）
- [x] PauseTimeout → 清理（`CleanupExpiredSessions` 自动处理）

---

## Phase C-3: Protocol Handshake Extension ✅

### C3.1 BuildHandshakeResponse 扩展
- [x] 格式扩展为 `[4B txIdLen][UTF8 txId][8B expiresAtUnixMs][1B flags]`
- [x] flags: bit 0 = reconnected

### C3.2 测试
- [x] 更新 `BuildHandshakeResponse_ShouldIncludeTxIdAndExpiry` 包含 flags 验证
- [x] 新增 `BuildHandshakeResponse_ShouldIncludeReconnectedFlag` 测试

---

## Phase D: 测试与回归 ✅

### D.1 新增测试验证
- [x] BlobStream: 80 → 81 测试（新增 reconnected flag 测试）

### D.2 全面回归
- [x] `dotnet test` 全部项目：**326 passed, 0 skipped**
  - Service: 31/0
  - BlobStream: 81/0
  - Coordinator: 126/0
  - Driver: 88/0
- [x] 构建 0 错误 0 警告

### D.3 工作记录
- [x] 更新 Work Summary
- [x] 更新 Plan.md 状态为 Complete
