# TODO — 2026-06-20 PGDriver 完成

> 按 Phase 顺序推进，每次只 focus 一个 task  
> 详细背景参见 [Plan.md](./Plan.md)

---

## Phase 0: 基础设施准备

- [x] **0.1** 运行现有测试，确认基线：319 pass / 3 skip
  - 解决 slnx 中缺少 Service 测试项目的问题
  - BlobStream: 80/0 | Service: 29/2 | Coordinator: 123/1 | Driver: 87/0
- [x] **0.2** 确认项目可构建
  - `dotnet build` 全部项目无错误
- [x] **0.3** 检查所有 `[Fact(Skip)]` 标记

---

## Phase 1: PgVectorDriver DataSource 模式修复

### 1.1 修复 EnsureExtensionAsync ✅

- [x] 分析 DataSource 模式下 `_connectionString` 为 null 的问题
- [x] 修复 `EnsureExtensionAsync` 支持 DataSource 路径
- [x] 编写测试暴露 DataSource 模式下的 bug（`SearchAsync_ShouldWorkWithDataSourceMode`）
- [x] 确认修复后集成测试通过（Driver 88/0）

---

## Phase 2: ServiceCollectionExtensions 更新

### 2.1 添加 DataSource 重载 ✅

- [x] `AddAdaptorPgVectorDriver(NpgsqlDataSource)` 重载
- [x] `AddAdaptorPostgreDrivers(NpgsqlDataSource)` 重载
- [x] 为 `PostgreSqlDriver` 和 `PostgresBlobDriver` 添加 DataSource 构造函数
- [x] 确认 DI 注册后 Driver 使用 DataSource 路径

### 2.2 保留向后兼容 ✅

- [x] `string connectionString` 重载保持可用
- [x] 现有代码不受影响（所有测试通过）

---

## Phase 3: Coordinator 语义修复

### 3.1 修复 CommitTransactionAsync ✅

- [x] 编写测试：对已回滚事务调用 Commit → 返回 `CommitResult(RolledBack)`
- [x] 修复 `CommitTransactionAsync`：FindEntry 返回 null 时返回 `RolledBack` 而非抛异常
- [x] 更新 Coordinatory 测试：`CommitTransactionAsync_OnUnknownId_ShouldReturnRolledBack` 替代旧 throw 测试
- [x] 更新 Service 测试：移除 `TransactionService_CommitOnRolledBackTx_ShouldReturnRolledBack` 的 Skip
- [x] 更新 Plugin 测试：`TransactionPlugin_CommitOnNonexistentTx_ShouldReturnRolledBack`
- [x] 确认 `RollbackTransactionAsync` 的 no-op 行为不受影响
- [x] 跳过测试从 3 减为 2

---

## Phase 4: 测试覆盖补充

### 4.1 pgvector-dotnet 类型映射测试 ✅

- [x] Phase 1 中已添加 `SearchAsync_ShouldWorkWithDataSourceMode` 测试
- [x] 使用 `NpgsqlDataSourceBuilder.UseVector()` 创建 DataSource
- [x] 验证 SearchAsync 在 DataSource 模式下正常工作

### 4.2 更新现有测试断言 ✅

- [x] `PgVectorDriverTest` 中无脆弱的格式化断言 - 均为基于设计的契约测试
- [x] 旧格式化方法（`DenseVectorToString`/`SparseVectorToString`）已在重构阶段删除

### 4.3 R6: Service gRPC BeginTransaction 测试 ✅

- [x] 注入 `IConnectionIdProvider` 接口，测试提供 `FixedConnectionIdProvider`
- [x] 移除 `[Fact(Skip)]`，测试现在正常通过

### 4.4 Session idle-timeout 测试优化 ✅

- [x] 测试中设置 `SessionCleanupInterval = 100ms`，将等待时间从 30.5s 减为 1.5s
- [x] 移除 `[Fact(Skip)]`，测试现在可自动运行
- [x] **至此 0 skipped** 🎉

---

## Phase 5: 代码质量改进

### 5.1 ReadLargeObjectAsync 参数重构 ✅

- [x] 参数从 5 个减为 4 个（用 `ConnectionEntry` 替代 `Connection`+`LocalTransaction`）
- [x] 更新调用处使用 `entry` 直接传递

### 5.2 OpenAsync 方法提取 ✅

- [x] 提取 `OpenCreateOrReplaceAsync` 处理 Create/CreateOrReplace 模式
- [x] 提取 `OpenExistingAsync` 处理 Read/Write/ReadWrite/Append 模式
- [x] `OpenAsync` 主体从 ~80 行减为 ~20 行

---

## Phase 6: 回归测试 & 收尾 ✅

- [x] **6.1** 全面回归测试
  - `dotnet test` 确认 **324 pass / 0 skip**（Service: 31/0, Coordinator: 125/0, BlobStream: 80/0, Driver: 88/0）
- [x] **6.2** 确认 0 new failures
- [x] **6.3** 更新 `Work Summary.md`
- [x] **6.4** 更新 `Plan.md` 状态为 Complete

---

## 已知问题跟踪 ✅ 全部关闭

| ID | 问题 | Phase | 状态 |
|:--:|------|:-----:|:----:|
| R1 | `AddAdaptorPgVectorDriver` 未创建 DataSource | 2 | ✅ 已解决 |
| R2 | `EnsureExtensionAsync` DataSource 模式下 `_connectionString` 为 null | 1 | ✅ 已修复 |
| R3 | `CommitTransactionAsync` 对已回滚事务抛异常 | 3 | ✅ 已修复 |
| R4 | 缺少 pgvector-dotnet 类型映射测试 | 4 | ✅ 已添加 |
| R5 | `ReadLargeObjectAsync` 5 参数需重构 | 5 | ✅ 已重构 |
| R6 | Service gRPC `BeginTransaction` 无法单元测试 | 4 | ✅ 已解决（注入 IConnectionIdProvider） |
| R7 | `PgVectorDriverTest` 向量参数断言需更新 | 4 | ✅ 无需修改 |
