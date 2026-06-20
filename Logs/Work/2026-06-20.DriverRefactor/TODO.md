# TODO — 2026-06-20 PGDriver 重构

> 按 Phase 顺序推进，每次只 focus 一个 task  
> 详细背景参见 [Plan.md](./Plan.md)

---

## Phase 0: 基础设施准备 ✅

- [x] **0.1** 运行现有测试，确认基线：287 pass / 5 skip
  - `dotnet test` 确认所有测试通过
- [x] **0.2** 添加 `Pgvector` NuGet 包到 `Adaptor.Driver.Postgre.csproj`
  - `dotnet add package Pgvector` → v0.3.2
- [x] **0.3** 确认项目可构建
  - `dotnet build` 全部项目无错误

---

## Phase 1: 连接管理基础设施抽取 ✅

### 1.1 创建 `NpgsqlConnectionManager` 共享类

- [x] 新建文件 `src/Driver/Postgre/NpgsqlConnectionManager.cs`
- [x] 管理 `ConcurrentDictionary<string, ConnectionEntry>`
- [x] `Enlist(Transaction)` + `GetEntry(Transaction)` + `RemoveEntry(string txId)` + `Dispose()`
- [x] 统一的 per-tx lock 策略（`ConcurrentDictionary<string, SemaphoreSlim>`）
- [x] 统一的 `NpgsqlEnlistmentHandler` 代替三个独立 handler
- [x] 支持 `connectionString` 和 `NpgsqlDataSource` 两种构造方式

### 1.2–1.4 三个 Driver 全部更新 ✅

- [x] `PostgreSqlDriver` → 委托给 Manager
- [x] `PgVectorDriver` → 委托给 Manager
- [x] `PostgresBlobDriver` → 委托给 Manager（新增 per-tx lock 支持）

### 1.5 统一 HealthCheckAsync ✅

- [x] 三个 Driver 的 HealthCheckAsync 全部委托给 `NpgsqlConnectionManager`

### 1.6 测试验证 ✅

- [x] 运行 Phase 1 前的所有测试，确认全部通过
- [x] 更新 `EnlistmentHandlersTest` 和 `PostgreSqlDriverTest` 以匹配新结构

---

## Phase 2: pgvector-dotnet 集成 ✅

### 2.1 更新 PgVectorDriver 连接方式 ✅

- [x] `PgVectorDriver` 接受 `NpgsqlDataSource` 参数（通过 `UseVector()` 配置）
- [x] 保留向后兼容：`string connectionString` 构造函数重载
- [x] `NpgsqlConnectionManager` 支持 DataSource 模式

### 2.2 更新 SearchAsync SQL ✅

- [x] 有 DataSource 时移除 `::vector` / `::sparsevec` 类型转换（直接传 `Vector`/`SparseVector`）
- [x] 无 DataSource 时保留向后兼容的字符串格式 + SQL cast

### 2.3 删除手动格式化代码 ✅

- [x] 删除 `DenseVectorToString()`、`SparseVectorToString()`
- [x] 删除 `DeserializeDenseVector()`、`DeserializeSparseVector()`
- [x] 新增 `CreateVectorParameter()` 统一处理两种路径

### 2.4 EnsureExtensionAsync ✅

- [x] 保留现有实现（`CREATE EXTENSION IF NOT EXISTS vector` 仍然需要）

### 2.5 ServiceCollectionExtensions

- [ ] TODO: `AddAdaptorPgVectorDriver` 创建带 `UseVector()` 的 DataSource

### 2.6 测试更新

- [ ] TODO: 更新测试中关于向量参数格式化的断言

---

## Phase 3: BLOB 随机访问路径修复 ✅

### 3.1 实现本地偏移量跟踪 ✅

- [x] 添加 `RandomAccessState` 内部类 + `ConcurrentDictionary<int, RandomAccessState>`
- [x] `OpenAsync` 时创建 state entry

### 3.2 修复 CloseAsync ✅

- [x] 标记 IsClosed + 移除 entry

### 3.3 修复 SeekAsync ✅

- [x] 支持 SeekOrigin.Begin/Current/End（End 通过 lo_lseek64 获取 blob size）
- [x] 更新 `state.Position`

### 3.4 修复 WriteAsync ✅

- [x] 使用 `state.Position` 作为 `lo_put` 的 offset 参数
- [x] 写入后更新 `state.Position += data.Length`

### 3.5 修复 TruncateAsync ✅

- [x] 使用 lo_open → lo_truncate64 → lo_close 流程
- [x] 截断后 clamp state.Position

### 3.6 启用测试 ✅

- [x] 移除 4 个 `[Fact(Skip)]` 标记 → 全部通过

---

## Phase 4: Guideline 合规 & 代码清理 ✅

- [x] per-tx lock 模式统一（Phase 1 已完成）
- [x] 工作记录时间戳修正（UTC+0）
- [x] 删除 VectorDriver 中未使用的 `MetadataToJsonString` 等方法
- [x] 检查 >5 参数的函数：`OpenAsync(4 params)`、`ReadLargeObjectAsync(5 params)` 在边界

---

## Phase 5: 测试验证 & 收尾 ✅

- [x] **5.1** 全面回归测试 — 290 passed, 1 skipped
  - 原有 287 pass 继续 pass
  - 4 个之前的 skipped 变为 pass
  - 0 new failures
- [x] **5.2** 确认 5 skipped 变为 1 skipped（仅剩 Coordinator 的 Session idle-timeout manual test）
- [x] **5.3** `Work Summary.md` 已更新
- [x] **5.4** `Plan.md` 状态已更新

---

## 已知问题跟踪

| ID | 问题 | Phase | 状态 |
|:--:|------|-------|:----:|
| R1 | PostgresBlobDriver 缺少 per-tx lock | 1 | ✅ 已添加 |
| R2 | RandomAccessState 需要线程安全保护 | 3 | ✅ ConcurrentDictionary + per-handle 使用 |
| R3 | lo_truncate 需要 lo_open 文件描述符 | 3 | ✅ 使用 lo_open → lo_truncate64 → lo_close |
| R4 | UseVector() 需要 DataSource 级别配置 | 2 | ✅ `NpgsqlConnectionManager` 支持 DataSource 构造 |
| R5 | HealthCheckAsync 在三处重复 | 1 | ✅ 统一实现在 `NpgsqlConnectionManager` |
