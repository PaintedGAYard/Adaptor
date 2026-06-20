# PGDriver 重构计划

> **日期**: 2026-06-20  
> **状态**: Complete  
> **对应任务**: TODO.md

---

## 目录

1. [背景](#1-背景)
2. [目标](#2-目标)
3. [当前状态分析](#3-当前状态分析)
4. [重构阶段划分](#4-重构阶段划分)
5. [阶段详情](#5-阶段详情)
6. [测试策略](#6-测试策略)
7. [风险评估](#7-风险评估)

---

## 1. 背景

Adaptor 项目是一个 .NET 10 gRPC 中间件服务，通过模块化的 Driver 架构协调 SQL、Vector、BLOB 三种异构存储的分布式事务。当前 PostgreSQL Driver 子系统包含三个 Driver：

| Driver | 能力 | 状态 |
|--------|------|------|
| `PostgreSqlDriver` | SQL Execute/Query | ✅ 功能完整 |
| `PgVectorDriver` | Vector Search | ✅ 功能完整，但使用手写 SQL 格式化向量 |
| `PostgresBlobDriver` | BLOB Upload/Download/RandomAccess | ⚠️ RandomAccess 路径有 4 个 placeholder |

### 前置工作

- 2026-06-18 — 架构设计完成，命名体系重构（Sql→Relational, Vector→RelationalVector）
- 2026-06-20 上午 — Phase 1-4 测试完成，243 个测试（236 passed, 7 skipped）
- 当前 codebase 处于早期快速开发阶段，存在以下可重构点

---

## 2. 目标

1. **pgvector-dotnet 集成** — 用 `Pgvector` NuGet package 的类型映射替代手写 SQL 向量格式化
2. **代码去重** — 抽取三个 Driver 共享的连接管理基础设施，消除重复模式
3. **BLOB 随机访问路径修复** — 实现 CloseAsync/SeekAsync/WriteAsync/TruncateAsync 的正确语义
4. **Guideline 合规** — 重构早期快速开发遗留的 codebase，使之符合 `Design/Working Guidelines.md` 要求
5. **测试驱动** — 先写测试，后实现；确保测试符合设计目标而非拟合实现行为

---

## 3. 当前状态分析

### 3.1 代码重复分析

三个 Driver 各自独立实现几乎相同的模式：

| 模式 | PostgreSqlDriver | PgVectorDriver | PostgresBlobDriver |
|------|:----------------:|:--------------:|:------------------:|
| `ConcurrentDictionary<string, ConnectionEntry> _connections` | ✅ | ✅ | ✅ |
| `Enlist(Transaction)` | ✅ | ✅ | ✅ |
| `GetEntry(Transaction)` | ✅ | ✅ | ✅ |
| `RemoveEntry(string txId)` | ✅ | ✅ | ✅ |
| `Dispose()` | ✅ | ✅ | ✅ |
| `HealthCheckAsync(CancellationToken)` | ✅ (identical) | ✅ (identical) | ✅ (identical) |
| `ConnectionEntry` record | ✅ | ✅ | ✅ |
| Per-tx lock (`SemaphoreSlim`) | ✅ | ✅ | ❌ (missing) |
| EnlistmentHandler class | `NpgsqlEnlistmentHandler` | `VectorEnlistmentHandler` | `BlobEnlistmentHandler` |

**问题**: 
- HealthCheckAsync 在三处完全相同的代码
- ConnectionEntry 定义重复三次
- EnlistmentHandler 的 Prepare/Commit/Rollback/InDoubt 逻辑几乎相同
- PostgresBlobDriver 缺少 per-tx lock（与其他两个不一致）

### 3.2 pgvector-dotnet 可以消除的代码

`PgVectorDriver` 中的以下手动实现可被 pgvector-dotnet 替代：

| 方法 | 行数 | 说明 |
|------|:----:|------|
| `DenseVectorToString(float[])` | 7 | 手动格式化 `[0.1,0.2,0.3]` |
| `SparseVectorToString(SparseVector)` | 10 | 手动格式化 `{1:0.1,2:0.2}` |
| `DeserializeDenseVector(string?)` | 11 | 手动解析向量字符串 |
| `DeserializeSparseVector(string?)` | 16 | 手动解析 sparsevec 字符串 |
| `DeserializeMetadata(string)` | 12 | JSON 解析（保留，与 pgvector 无关） |
| `MetadataToJsonString(...)` | 18 | JSON 序列化（保留，与 pgvector 无关） |
| SQL 中的 `::vector` / `::sparsevec` 类型转换 | — | 参数化传递 `Vector`/`SparseVector` 对象后不再需要 |

### 3.3 BLOB 随机访问路径问题

当前实现使用 PG17 推荐的 `lo_get`/`lo_put` server-side API，但这些 API 不维护隐式位置游标。导致：

| 操作 | 设计行为 | 当前实现 | 修复方案 |
|------|---------|---------|---------|
| `CloseAsync` | 释放描述符，后续读取应失败 | no-op | 维护 open-instance 状态，Close 后标记无效 |
| `SeekAsync` | 重新定位读写偏移量 | 返回 offset 但不影响读取 | 维护每个实例的 local offset |
| `WriteAsync` | 在当前偏移量写入 | 始终在 offset 0 写入 | 使用 local offset + `lo_put(oid, offset, data)` |
| `TruncateAsync` | 截断 blob 到指定长度 | no-op stub | 使用 `lo_truncate(fd, length)` |

### 3.4 Guideline 合规问题

- **文档风格**: 部分 XML doc 过于详细（不应包含实现细节）
- **函数参数**: `OpenAsync(4 params)`、`ExecuteOnCapabilityAsync` 等接近边界
- **字符串拼接 SQL**: `$""` 中使用 `{DefaultTableName}` 是表名拼接，不是参数注入，可接受但需注意约束
- **Per-tx lock 不一致**: PostgresBlobDriver 缺少 SemaphoreSlim 保护

---

## 4. 重构阶段划分

```
Phase 0: 基础设施准备
  └─ 添加 Pgvector NuGet 包
  └─ 建立测试基线（确保重构前所有测试通过）

Phase 1: 连接管理基础设施抽取
  └─ 创建 NpgsqlConnectionManager（连接注册+事务 enlistment+清理）
  └─ 三个 Driver 使用共享组件
  └─ 统一 per-tx lock 策略

Phase 2: pgvector-dotnet 集成
  └─ 更新 PgVectorDriver 使用 Vector/SparseVector 类型
  └─ 删除手动格式化代码
  └─ 更新 Npgsql 连接创建方式（使用 UseVector DataSource）

Phase 3: BLOB 随机访问路径修复
  └─ 实现 local offset tracking
  └─ 修复 CloseAsync/SeekAsync/WriteAsync/TruncateAsync
  └─ 移除 [Fact(Skip)] 标记

Phase 4: Guideline 合规 & 代码清理
  └─ 统一文档风格
  └─ 其他杂项清理

Phase 5: 测试验证 & 收尾
  └─ 全面回归测试
  └─ Work Summary
```

---

## 5. 阶段详情

### Phase 0: 基础设施准备

**目标**: 确保在重构前有可靠的测试基线

| 任务 | 说明 |
|------|------|
| 0.1 | 运行所有现有测试，确认 236 pass / 7 skip |
| 0.2 | 添加 `Pgvector` NuGet 包到 `Adaptor.Driver.Postgre.csproj` |
| 0.3 | 确认项目可构建 |

### Phase 1: 连接管理基础设施抽取

**目标**: 消除三个 Driver 间的代码重复

**设计决策**: 遵循 Composition over Inheritance 原则，不创建抽象基类，而是创建一个独立的 `NpgsqlConnectionManager` 服务类，通过组合方式提供给各 Driver。

**NpgsqlConnectionManager 职责**:
- 管理 `ConcurrentDictionary<string, ConnectionEntry>` 
- `Enlist(Transaction)` — 创建连接 + 本地事务 + 注册 volatile enlistment
- `GetEntry(Transaction)` — 查找已注册的连接
- `RemoveEntry(string txId)` — 清理事务资源
- `EnsureExtensionAsync` — 创建 pgvector 扩展（非事务连接）
- `Dispose()` — 清理所有连接
- 线程安全性（per-tx lock）

**每个 Driver 保留的独特逻辑**:
- `PostgreSqlDriver`: ExecuteAsync / QueryAsync 的具体 SQL 执行
- `PgVectorDriver`: SearchAsync 的具体搜索逻辑、EnsureTableAsync
- `PostgresBlobDriver`: UploadAsync / DownloadAsync 的具体逻辑、ReadLargeObjectAsync

### Phase 2: pgvector-dotnet 集成

**目标**: 替换 PgVectorDriver 中的手写 SQL 向量格式化

**变更要点**:

1. **连接创建方式变更**:
   - 当前: `new NpgsqlConnection(connectionString)`
   - 新: 使用 `NpgsqlDataSourceBuilder` + `UseVector()` 创建 DataSource，从 DataSource 获取连接
   - 不需要在所有路径都使用 DataSource — 仅 PgVectorDriver 需要 `UseVector()` 类型映射
   
2. **SQL 查询变更**:
   - 当前: `@vector ::vector` 字符串转换
   - 新: 直接传递 `Vector` / `SparseVector` 对象作为参数

3. **删除的方法**:
   - `DenseVectorToString()` — 用 `new Vector(float[])` 替代
   - `SparseVectorToString()` — 用 `new SparseVector(float[])` 或 `new SparseVector(dict, dims)` 替代
   - `DeserializeDenseVector()` — 用 `Vector.ToString()` + `Vector` 构造函数替代
   - `DeserializeSparseVector()` — 用 `SparseVector.ToString()` + `SparseVector` 构造函数替代

**保留的方法**:
   - `MetadataToJsonString()` — 与 pgvector 无关
   - `DeserializeMetadata()` — 与 pgvector 无关

**架构考量**:
- `UseVector()` 在 DataSource 级别配置，因此需要让 PgVectorDriver 使用 DataSource 而非 raw connection string
- 但 PostgreSqlDriver 和 PostgresBlobDriver 不需要 `UseVector()`，它们可以继续使用 raw connection string
- 方案：PgVectorDriver 接受一个 `NpgsqlDataSource` 参数（通过 `UseVector()` 配置），其他 Driver 继续使用 `string connectionString`

### Phase 3: BLOB 随机访问路径修复

**目标**: 实现 `IBlobRandomAccessCapability` 的完整语义

**设计方案**:

维护一个 per-open-instance 的偏移量跟踪器。当 `OpenAsync` 被调用时，在 Driver 内部创建一个 state entry 记录 OID 和当前偏移量。后续的 Read/Write/Seek 操作更新这个状态。

```csharp
// Driver 内部新增
private sealed class RandomAccessState
{
    public int Oid { get; init; }
    public long Position { get; set; }
    public bool IsClosed { get; set; }
}

private readonly ConcurrentDictionary<int, RandomAccessState> _randomAccessStates = new();
```

- **OpenAsync**: 创建 state entry，position = 0
- **CloseAsync**: 标记 IsClosed = true，移除 state entry
- **SeekAsync**: 更新 `state.Position`，计算新位置
- **ReadAsync**: `lo_get(oid, state.Position, count)` → 读取后更新 `state.Position += bytesRead`
- **WriteAsync**: `lo_put(oid, state.Position, data)` → 写入后更新 `state.Position += data.Length`
- **TruncateAsync**: `lo_truncate(fd, length)` — 需要先 lo_open 再 lo_truncate

**注意**: loFd 参数实际上是 OID（当前实现的约定），无需 lo_open。但 `lo_truncate` 需要文件描述符，所以 TruncateAsync 需要 lo_open + lo_truncate + lo_close 三步。

**更新测试**:
- 移除 `[Fact(Skip)]` 标记，使 CloseAsync_ShouldReleaseDescriptor、SeekAsync_ShouldAffectSubsequentReadPosition、WriteAsync_ShouldRespectSeekPosition、TruncateAsync_ShouldShortenBlob 变为 active

### Phase 4: Guideline 合规 & 代码清理

- 简化 XML doc（聚焦接口契约，不包含实现细节）
- 确保 per-tx lock 在所有三个 Driver 中一致
- 检查函数参数数量（`OpenAsync(4 params)` 在边界内）

### Phase 5: 测试验证 & 收尾

- 全面回归测试
- 验证 7 个 skipped 测试中 4 个变为 pass
- 编写 Work Summary

---

## 6. 测试策略

| 层级 | 策略 | 工具 |
|------|------|------|
| **单元测试** | 重构前确保行为不变；新增实现先写测试 | xUnit + NSubstitute |
| **集成测试** | 使用 Testcontainers 验证真实 PG 行为 | Testcontainers.PostgreSql |
| **回归测试** | 每个 Phase 完成后运行全套测试 | `dotnet test` |

### 测试变更计划

| Phase | 测试变更 |
|-------|---------|
| Phase 0 | 保持现有测试不变，验证基线 |
| Phase 1 | 现有测试应继续通过（行为不变）|
| Phase 2 | 更新 PgVectorDriver 测试中关于向量格式化的断言；新增 pgvector-dotnet 类型映射测试 |
| Phase 3 | 移除 4 个 `[Fact(Skip)]` 标记，确认它们变为 pass |
| Phase 4 | 无需测试变更 |

---

## 7. 风险评估

| 风险 | 概率 | 影响 | 缓解措施 |
|------|:----:|:----:|---------|
| pgvector-dotnet UseVector() 需要 DataSource 级别配置，与现有连接管理方式冲突 | 高 | 中 | 仅 PgVectorDriver 使用 DataSource，其他 Driver 保持不变 |
| lo_truncate 需要文件描述符（当前 loFd 是 OID） | 中 | 中 | TruncateAsync 内部执行 lo_open + lo_truncate + lo_close |
| 重构后现有测试可能因内部实现变更而需要调整 | 中 | 低 | 关注行为不变性（black-box testing） |
| 跨多个 Driver 的共享组件可能引入线程安全问题 | 低 | 高 | NpgsqlConnectionManager 使用 ConcurrentDictionary + SemaphoreSlim 保护 |
