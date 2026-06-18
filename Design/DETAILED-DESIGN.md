# Adaptor — 详细设计文档

> 版本: 1.0  
> 日期: 2026-06-18  
> 状态: Draft

---

## 目录

1. [项目概述](#1-项目概述)
2. [系统架构](#2-系统架构)
3. [gRPC 协议设计](#3-grpc-协议设计)
4. [事务协调器](#4-事务协调器)
5. [Driver 接口设计](#5-driver-接口设计)
6. [错误处理与重试](#6-错误处理与重试)
7. [连接与会话管理](#7-连接与会话管理)
8. [项目结构](#8-项目结构)
9. [尚未解决的问题](#9-尚未解决的问题)

---

## 1. 项目概述

### 1.1 背景

许多现代运行时（Python、JavaScript、Go 等）缺乏成熟的分布式事务基础设施（DTC）。当应用需要同时操作 **SQL 数据库**、**向量数据库** 和 **BLOB 存储** 时，跨这三种异构存储的一致性事务管理变得极其复杂。

### 1.2 目标

Adaptor 是一个基于 .NET 10 的独立中间件服务，面向**缺乏 DTC 设施的运行时**，通过自定义 gRPC 协议暴露统一的分布式事务语义。其核心职责是：

1. **向下** — 通过模块化的 Driver 接口统一控制 SQL DB、Vector DB、BLOB Storage 三种存储设施
2. **向上** — 通过统一的 gRPC 协议暴露简化的 `Begin` / `Commit` / `Rollback` 事务语义
3. **内部** — 利用 .NET 的 `System.Transactions` 基础设施，将多个异构存储的事务生命周期聚合为单个可协调的事务

### 1.3 核心设计原则

| 原则 | 说明 |
|------|------|
| **操作语义分离** | SQL、Vector、BLOB 各自保留独立的操作 API，不强行统一 |
| **事务语义统一** | 三者的事务生命周期通过中间件协调为统一的 Begin/Commit/Rollback |
| **All or Nothing** | 事务最终要么全部成功，要么全部回滚 |
| **Driver 负责事务** | Driver 实现完整的分布式事务支持（.NET 标准接口），中间件只负责聚合 |
| **无自定义事务类型** | 仅在 .NET 内置设施不满足需求时才考虑自定义 Transaction 子类 |
| **Composition over Inheritance** | 不定义大一统的 Driver 接口；能力通过正交的独立接口暴露，Driver 按需组合 |
| **Semantic Kernel 技术栈** | Driver 和中间件均基于 `Microsoft.SemanticKernel` 构建，利用其 Plugin/Connector 模型 |

---

## 2. 系统架构

### 2.1 分层架构

```
┌──────────────────────────────────────────────────────────┐
│  Consumer Layer                                           │
│  (Python / JavaScript / Go / ... 缺乏 DTC 的运行时)      │
│                                                            │
│  tx = client.Begin()                                       │
│  client.SqlQuery(tx, "SELECT ...")                        │
│  client.VectorSearch(tx, embedding)                        │
│  client.BlobUpload(tx, "file.bin", data)                   │
│  client.Commit(tx)    /    client.Rollback(tx)             │
└──────────────────────────┬───────────────────────────────┘
                           │ gRPC (自定义协议)
┌──────────────────────────▼───────────────────────────────┐
│  Adaptor Service Layer                                    │
│  (.NET 10 gRPC 服务)                                      │
│                                                            │
│  ┌────────────────────────────────────────────────────┐   │
│  │  TransactionCoordinator                             │   │
│  │  ┌──────────────────────────────────────────────┐  │   │
│  │  │  System.Transactions Abstraction              │  │   │
│  │  │  - CommittableTransaction                     │  │   │
│  │  │  - TransactionScope (内部使用)                 │  │   │
│  │  │  - DependentTransaction (如需嵌套)             │  │   │
│  │  └──────────────────────────────────────────────┘  │   │
│  │  ┌──────────────────────────────────────────────┐  │   │
│  │  │  All or Nothing 策略引擎                      │  │   │
│  │  │  - Prepare Phase (投票)                      │  │   │
│  │  │  - Commit Phase (决定 + 重试)                │  │   │
│  │  │  - Rollback Phase (补偿)                     │  │   │
│  │  └──────────────────────────────────────────────┘  │   │
│  │  ┌──────────────────────────────────────────────┐  │   │
│  │  │  Session / KeepAlive / Timeout 管理          │  │   │
│  │  └──────────────────────────────────────────────┘  │   │
│  └────────────────────────────────────────────────────┘   │
│                                                            │
│  ┌────────────────────────────────────────────────────┐   │
│  │  Semantic Kernel Orchestration                     │   │
│  │  (Kernel + KernelPlugin + KernelFunction)          │   │
│  └────────────────────────────────────────────────────┘   │
│                                                            │
│  ┌──────────────┬──────────────┬──────────────────────┐   │
│  │  SQL          │  Vector       │  BLOB                 │   │
│  │  Resource     │  Resource     │  Resource             │   │
│  │  Manager      │  Manager      │  Manager              │   │
│  ├──────────────┤├──────────────┤├──────────────────────┤   │
│  │ IResourceManager              │ IResourceManager          │
│  │ + ITransactionalResource      │ + ITransactionalResource  │
│  │ + ISqlExecuteCapability       │ + IBlobUploadCapability   │
│  │ + ISqlQueryCapability         │ + IBlobDownloadCapability │
│  │ + IHealthCheckCapability      │ + IHealthCheckCapability  │
│  └──────┬───────┴──────┬───────┴──────────┬───────────┘   │
│         │              │                  │                │
│  ┌──────▼──────┐ ┌─────▼──────┐  ┌────────▼────────┐     │
│  │ PgSQL       │ │ pgvector   │  │ BYTEA + LO      │     │
│  │ MySQL       │ │ Milvus     │  │ AWS S3 + 补偿   │     │
│  │ ...         │ │ ...        │  │ ...             │     │
│  └─────────────┘ └────────────┘  └─────────────────┘     │
└──────────────────────────────────────────────────────────┘
```

### 2.2 关键概念

| 概念 | 定义 |
|------|------|
| **Transaction** | .NET `System.Transactions.Transaction` 实例，代表一个工作单元。中间件内部使用 `CommittableTransaction` 作为顶层事务 |
| **Resource Manager (RM)** | 分布式事务的基本参与单元。每个 Driver 是一个 RM，管理一种存储资源，通过 `IEnlistmentNotification` 参与两阶段提交 |
| **Enlistment** | RM (Driver) 将自身注册到 .NET Transaction 的过程 |
| **Session** | Consumer 与中间件之间的 gRPC 会话上下文，绑定到一个活跃事务 |
| **Driver** | 实现 `IResourceManager` 的适配器，通过组合独立的能力接口对外暴露操作，基于 Semantic Kernel Plugin 模型加载 |

### 2.3 事务生命周期

```
Consumer              Middleware              Driver 1 (SQL)      Driver 2 (Vector)    Driver 3 (BLOB)
   │                      │                       │                   │                   │
   │── BeginTransaction ──│                       │                   │                   │
   │                      │── 创建 .NET CommittableTransaction        │                   │
   │                      │── 注册到事务表                            │                   │
   │<── tx_id ────────────│                       │                   │                   │
   │                      │                       │                   │                   │
   │── SqlQuery(tx_id) ───│                       │                   │                   │
   │                      │── Enlist Driver 1 ──▶ │── 开始本地事务    │                   │
   │                      │                       │── Prepare() OK    │                   │
   │<── result ───────────│                       │                   │                   │
   │                      │                       │                   │                   │
   │── VectorSearch(tx_id)│                       │                   │                   │
   │                      │── Enlist Driver 2 ────│─────────────────▶ │── 开始本地事务   │
   │                      │                       │                   │── Prepare() OK   │
   │<── result ───────────│                       │                   │                   │
   │                      │                       │                   │                   │
   │── BlobUpload(tx_id)  │                       │                   │                   │
   │                      │── Enlist Driver 3 ────│───────────────────│─────────────────▶│── 开始本地事务
   │                      │                       │                   │                   │── Prepare() OK
   │<── result ───────────│                       │                   │                   │
   │                      │                       │                   │                   │
   │── Commit(tx_id) ─────│                       │                   │                   │
   │                      │── Phase 1: Prepare    │                   │                   │
   │                      │── Driver 1 ──────────▶│── Prepare ◀──────│── Prepare ◀───────│── Prepare
   │                      │── Driver 2 ───────────│──────────────────▶│                   │
   │                      │── Driver 3 ───────────│───────────────────│──────────────────▶│
   │                      │                       │                   │                   │
   │                      │◀── All Prepared ──────│◀── OK ───────────│◀── OK ────────────│◀── OK
   │                      │                       │                   │                   │
   │                      │── Phase 2: Commit     │                   │                   │
   │                      │── Driver 1 ──────────▶│── Commit ◀───────│── Commit ◀────────│── Commit
   │                      │── Driver 2 ───────────│──────────────────▶│                   │
   │                      │── Driver 3 ───────────│───────────────────│──────────────────▶│
   │                      │                       │                   │                   │
   │                      │◀── All Committed ─────│◀── OK ───────────│◀── OK ────────────│◀── OK
   │                      │── 释放 .NET Transaction                    │                   │
   │<── OK ───────────────│                       │                   │                   │
```

---

## 3. gRPC 协议设计

### 3.1 协议定位

gRPC 的 `service` 关键字在此项目中**不是自包含的 service 单元**，仅作为 **endpoint group**。真正的 service 单元是**围绕中间件事务对象生命周期管理形成的 API 整体**。

### 3.2 服务组织

一个统一的 gRPC namespace，其中包含以下端点：

```
namespace Adaptor;

service Transaction {
  // ─── 事务生命周期（核心） ───
  rpc BeginTransaction(BeginTransactionRequest) returns (BeginTransactionResponse);
  rpc CommitTransaction(CommitTransactionRequest) returns (CommitTransactionResponse);
  rpc RollbackTransaction(RollbackTransactionRequest) returns (RollbackTransactionResponse);
  // 查询事务状态（映射自 System.Transactions.TransactionStatus）
  rpc GetTransactionStatus(GetTransactionStatusRequest) returns (GetTransactionStatusResponse);
}

service DBSQL {
  // ─── SQL 操作 ───
  rpc SqlExecute(SqlExecuteRequest) returns (SqlExecuteResponse);
  rpc SqlQuery(SqlQueryRequest) returns (SqlQueryResponse);
  rpc SqlExecuteBatch(SqlExecuteBatchRequest) returns (SqlExecuteBatchResponse);
}

service DBVector {
  // ─── Vector 操作 ───
  rpc VectorUpsert(VectorUpsertRequest) returns (VectorUpsertResponse);
  rpc VectorSearch(VectorSearchRequest) returns (VectorSearchResponse);
  rpc VectorDelete(VectorDeleteRequest) returns (VectorDeleteResponse);
  rpc VectorListCollections(VectorListCollectionsRequest) returns (VectorListCollectionsResponse);
}

service DBBLOB {
  // ─── BLOB 操作 ───
  rpc BlobUpload(BlobUploadRequest) returns (BlobUploadResponse);
  rpc BlobDownload(BlobDownloadRequest) returns (BlobDownloadResponse);
  rpc BlobDelete(BlobDeleteRequest) returns (BlobDeleteResponse);
  rpc BlobList(BlobListRequest) returns (BlobListResponse);
}

// ...
```

### 3.3 事务上下文传递

所有数据操作 RPC 的消息中必须包含事务上下文：

```protobuf
message TransactionContext {
  string transaction_id = 1;    // BeginTransaction 返回的 ID
  // 可选：未来版本可能扩展 ambient 事务支持
}
```

### 3.4 事务生命周期 RPC

```protobuf
// ─── Begin ───
message BeginTransactionRequest {
  google.protobuf.Duration timeout = 1;  // 可选，事务超时时间
  map<string, string> metadata = 2;      // 可选，自定义元数据
}

message BeginTransactionResponse {
  string transaction_id = 1;
  google.protobuf.Timestamp expires_at = 2;
}

// ─── Commit ───
message CommitTransactionRequest {
  string transaction_id = 1;
}

message CommitTransactionResponse {
  CommitStatus status = 1;
  repeated DriverCommitResult driver_results = 2;  // 各 Driver 的提交结果
  string error_message = 3;
}

enum CommitStatus {
  COMMIT_STATUS_UNSPECIFIED = 0;
  COMMIT_STATUS_COMMITTED = 1;       // 全部成功
  COMMIT_STATUS_PARTIAL = 2;         // 部分成功（重试耗尽后）
  COMMIT_STATUS_ROLLED_BACK = 3;     // 已回滚
  COMMIT_STATUS_TIMEOUT = 4;         // 超时
}

message DriverCommitResult {
  string driver_name = 1;
  bool success = 2;
  int32 retry_count = 3;
  string error_message = 4;
}

// ─── Rollback ───
message RollbackTransactionRequest {
  string transaction_id = 1;
}

message RollbackTransactionResponse {
  bool success = 1;
  string error_message = 2;
}

// ─── 状态查询 ───
message GetTransactionStatusRequest {
  string transaction_id = 1;
}

message GetTransactionStatusResponse {
  TransactionState state = 1;
  google.protobuf.Timestamp expires_at = 2;  // 一次性计算，不维护
}

enum TransactionState {
  TRANSACTION_STATE_UNSPECIFIED = 0;
  TRANSACTION_STATE_ACTIVE = 1;
  TRANSACTION_STATE_COMMITTED = 2;
  TRANSACTION_STATE_ROLLED_BACK = 3;
  TRANSACTION_STATE_IN_DOUBT = 4;
}

```

### 3.5 连接与 KeepAlive

- 使用 gRPC 内置的 HTTP/2 keepalive ping
- 超时在 `BeginTransaction` 时通过 `timeout` 参数指定（可选，默认 30s）
- 如需长时间操作，Consumer 应创建超时无限的独立事务
- 中间件服务端配置默认事务超时 + 会话空闲超时
- 超时未 commit 的事务自动回滚（由 CommittableTransaction 内建机制处理）

---

## 4. 事务协调器

### 4.1 核心职责

`TransactionCoordinator` 是中间件的核心组件，负责：

1.  管理 .NET `CommittableTransaction` 的生命周期
2.  协调多个 Driver 的 Enlistment
3.  执行 All or Nothing 策略（Prepare → Commit/Rollback）
4.  处理超时和自动回滚
5.  管理重试逻辑

### 4.2 System.Transactions 使用策略

```
┌──────────────────────────────────────────────────────┐
│  TransactionCoordinator                               │
│                                                        │
│  1. BeginTransaction                                   │
│     → 创建 CommittableTransaction                      │
│     → 配置超时                                         │
│     → 生成 transaction_id 并注册到事务表               │
│                                                        │
│  2. 数据操作（SqlQuery / VectorSearch / BlobUpload）   │
│     → 按需 Enlist Driver（延迟 enlistment）             │
│     → 每个 Driver 在自己的线程/任务中：                │
│       a. 调用 driver.Enlist(transaction)               │
│       b. Driver 内部开始本地事务                       │
│       c. Driver 调用 transaction.EnlistVolatile/       │
│          EnlistDurable 注册自己                        │
│       d. 执行操作                                      │
│                                                        │
│  3. CommitTransaction                                  │
│     → 调用 transaction.Commit()                        │
│     → .NET 内部触发两阶段提交：                        │
│       a. 每个 Driver 的 Prepare()                      │
│       b. 全部通过后，每个 Driver 的 Commit()           │
│     → 任一 Prepare 失败 → Rollback                     │
│                                                        │
│  4. RollbackTransaction                                │
│     → 调用 transaction.Rollback()                      │
│     → .NET 通知所有 Driver 回滚                       │
│                                                        │
│  5. 超时处理                                           │
│     → CommittableTransaction 超时自动触发 Rollback     │
│     → 所有 Enlisted Driver 收到 Rollback 通知          │
└──────────────────────────────────────────────────────┘
```

**关键说明：无需自定义 Transaction 子类**

.NET 的 `System.Transactions` 已经提供了完整的分布式事务协调基础设施：
- `CommittableTransaction` — 顶层可提交事务
- `Transaction.EnlistVolatile` / `EnlistDurable` — 驱动程序化注册
- `IEnlistmentNotification` — 两阶段提交回调接口（`Prepare` / `Commit` / `Rollback` / `InDoubt`）
- `IPromotableSinglePhaseNotification` — 可提升单阶段提交（PSPE）
- `TransactionScope` — 方便内部使用的 ambient 事务范围

除非上述接口在某场景（如非关系型存储的高级事务协调）中暴露出不足，否则**不引入自定义 Transaction 子类**。

### 4.3 事务隔离级别

默认使用 `System.Transactions.IsolationLevel.ReadCommitted`。
后续可根据 Driver 能力暴露可配置的隔离级别选择。

### 4.4 事务状态

事务状态直接使用 .NET `System.Transactions.TransactionStatus` 枚举：

| TransactionStatus | 说明 |
|-------------------|------|
| `Active` | 事务活跃，可执行数据操作 |
| `Committed` | 事务已成功提交 |
| `Aborted` | 事务已回滚 |
| `InDoubt` | 事务结果不确定（极少发生） |

Coordinator 不维护中间状态（Preparing / Committing / RollingBack 等），
这些状态由 .NET DTC 基础设施在内部管理，不对外暴露。

```
                         ┌─────────┐
                         │  ACTIVE  │ ◀── BeginTransaction
                         └────┬─────┘
                              │
                    ┌─────────┼─────────┐
                    │         │         │
               Data Op    Commit    Rollback
                    │         │         │
                    ▼         ▼         ▼
               (Enlist)  ┌──────────┐  │
                         │ .NET 内部│  │
                         │ 协调 2PC │  │
                         └────┬─────┘  │
                              │         │
                    ┌─────────┼─────────┘
                    │         │
               ┌────▼────┐ ┌─▼────────┐
               │COMMITTED│ │ ABORTED  │
               │(终态)   │ │ (终态)   │
               └─────────┘ └──────────┘

           IN_DOUBT 为极罕见的终态，仅当与 MSDTC
           通信失败且无法恢复时进入。
```


---

## 5. Driver 接口设计

### 5.1 设计原则

1. **Resource Manager 模式** — 每个 Driver 是一个 Resource Manager (RM)，管理一种存储资源，参与 .NET `System.Transactions` 两阶段提交协调
2. **Composition over Inheritance** — 不定义大一统的 `IStorageDriver` 接口；能力通过正交的独立接口暴露，Driver 按需组合
3. **Semantic Kernel 技术栈** — Driver 和中间件均基于 `Microsoft.SemanticKernel` 构建，利用其 Plugin/Connector 模型和 DI 基础设施

### 5.2 Resource Manager 模式

在 .NET `System.Transactions` 中，Resource Manager (RM) 是参与分布式事务的基本单元：

```
┌─────────────────────────────────────────────────────────┐
│  TransactionCoordinator (Transaction Manager)            │
│                                                          │
│  ┌───────────────────────────────────────────────────┐  │
│  │  System.Transactions                              │  │
│  │  ┌──────────────┐  ┌──────────────┐  ┌────────┐ │  │
│  │  │ Committable  │  │ Enlistment   │  │ Two-   │ │  │
│  │  │ Transaction  │  │ Notification │  │ Phase  │ │  │
│  │  └──────────────┘  └──────────────┘  └────────┘ │  │
│  └───────────────────────────────────────────────────┘  │
│              │ Enlist          │ Enlist          │ Enlist│
│     ┌────────▼────┐   ┌────────▼────┐   ┌────────▼────┐│
│     │ SQL RM      │   │ Vector RM   │   │ BLOB RM     ││
│     │ (Driver)    │   │ (Driver)    │   │ (Driver)    ││
│     └─────────────┘   └─────────────┘   └─────────────┘│
└─────────────────────────────────────────────────────────┘
```

每个 Driver (RM) 必须：
- 实现 `IEnlistmentNotification`（两阶段提交回调接口）
- 通过 `Transaction.EnlistVolatile()` / `EnlistDurable()` 注册自身
- 在 `Prepare()` 中投票，在 `Commit()` / `Rollback()` 中执行最终操作

### 5.3 能力接口体系（Composition）

Driver 的能力通过独立的接口暴露，各接口职责正交、可独立实现：

```csharp
/// <summary>
/// 最基础标记：该 Driver 是一个资源管理器。
/// 所有 Driver 必须实现此接口。
/// </summary>
public interface IResourceManager
{
    string Name { get; }
    ResourceType ResourceType { get; }
}

[Flags]
public enum ResourceType
{
    Sql,
    Vector,
    Blob,
}

/// <summary>
/// 声明该 Driver 支持事务性 Enlistment。
/// 只有实现此接口的 Driver 才会参与分布式事务协调。
/// </summary>
public interface ITransactionalResourceManager : IResourceManager
{
    void Enlist(Transaction transaction);
}
```

能力接口（按存储类型和应用场景分离）：

```csharp
// ─── SQL 能力 ───
public interface ISqlExecuteCapability
{
    Task<SqlExecuteResult> ExecuteAsync(
        SqlExecuteRequest request, CancellationToken ct);
}

public interface ISqlQueryCapability
{
    Task<SqlQueryResult> QueryAsync(
        SqlQueryRequest request, CancellationToken ct);
}

// ─── Vector 能力 ───
public interface IVectorUpsertCapability
{
    Task<VectorUpsertResult> UpsertAsync(
        VectorUpsertRequest request, CancellationToken ct);
}

public interface IVectorSearchCapability
{
    Task<VectorSearchResult> SearchAsync(
        VectorSearchRequest request, CancellationToken ct);
}

// ─── BLOB 能力 ───
public interface IBlobUploadCapability
{
    Task<BlobUploadResult> UploadAsync(
        BlobUploadRequest request, CancellationToken ct);
}

public interface IBlobDownloadCapability
{
    Task<BlobDownloadResult> DownloadAsync(
        BlobDownloadRequest request, CancellationToken ct);
}

// ─── 公共能力 ───
public interface IHealthCheckCapability
{
    Task<bool> HealthCheckAsync(CancellationToken ct);
}
```

### 5.4 Driver 实现范例（Composition in action）

Driver 不继承、不实现任何"大一统"接口，而是按需选择能力接口：

```csharp
// ── PostgreSQL Driver：SQL RM + SQL Execute/Query + HealthCheck ──
public sealed class PostgreSqlDriver :
    IResourceManager,
    ITransactionalResourceManager,  // 参与分布式事务
    ISqlExecuteCapability,
    ISqlQueryCapability,
    IHealthCheckCapability
{
    public string Name => "PostgreSQL";
    public ResourceType ResourceType => ResourceType.Sql;

    // ITransactionalResourceManager
    public void Enlist(Transaction transaction)
    {
        // 将 NpgsqlConnection enlist 到 System.Transactions
    }

    // IEnlistmentNotification（显式接口实现）
    void IEnlistmentNotification.Prepare(PreparingEnlistment pe)
    {
        try { /* 验证本地事务可提交 */ pe.Prepared(); }
        catch { pe.ForceRollback(); }
    }
    void IEnlistmentNotification.Commit(Enlistment e) { /* 提交 */ e.Done(); }
    void IEnlistmentNotification.Rollback(Enlistment e) { /* 回滚 */ e.Done(); }
    void IEnlistmentNotification.InDoubt(Enlistment e) { /* 记录 */ e.Done(); }

    // ISqlQueryCapability
    public Task<SqlQueryResult> QueryAsync(SqlQueryRequest request, CancellationToken ct)
    {
        // 实际的 SQL 查询逻辑
    }

    // ISqlExecuteCapability
    public Task<SqlExecuteResult> ExecuteAsync(SqlExecuteRequest request, CancellationToken ct) { /* ... */ }

    // IHealthCheckCapability
    public Task<bool> HealthCheckAsync(CancellationToken ct) { /* ... */ }
}

// ── AWS S3 Driver：BLOB RM + Upload/Download/Delete + HealthCheck ──
// 注意：S3 没有原生事务支持，Driver应自行实现事务逻辑
public sealed class S3BlobDriver :
    IResourceManager,
    ITransactionalResourceManager,  // 应用层补偿事务
    IBlobUploadCapability,
    IBlobDownloadCapability,
    IHealthCheckCapability
{
    public string Name => "AWS S3";
    public ResourceType ResourceType => ResourceType.Blob;
    // ... 类似实现
}
```

### 5.5 Semantic Kernel 集成

Adaptor 基于 `Microsoft.SemanticKernel` 技术栈构建，利用其 Plugin/Connector 模型作为 Driver 的承载框架：

```
┌──────────────────────────────────────────────────────────┐
│  Adaptor Middleware                                       │
│                                                            │
│  ┌──────────────────── Kernel ─────────────────────────┐  │
│  │                                                      │  │
│  │  Plugins:                                            │  │
│  │  ┌──────────┐  ┌──────────┐  ┌──────────┐          │  │
│  │  │PostgreSQL│  │pgvector  │  │S3 BLOB   │  ← KernelPlugin │
│  │  │Plugin    │  │Plugin    │  │Plugin    │          │  │
│  │  │          │  │          │  │          │          │  │
│  │  │• Query   │  │• Search  │  │• Upload  │  ← KernelFunction│
│  │  │• Execute │  │• Upsert  │  │• Download│          │  │
│  │  └──────────┘  └──────────┘  └──────────┘          │  │
│  │                                                      │  │
│  │  Services (DI):                                      │  │
│  │  ┌──────────────────────────────────────────────┐   │  │
│  │  │ TransactionCoordinator                        │   │  │
│  │  │ SessionManager                                │   │  │
│  │  └──────────────────────────────────────────────┘   │  │
│  └──────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

```csharp
// 1. Driver 作为 IResourceManager 注册到 Kernel 的 DI 容器
IKernelBuilder builder = Kernel.CreateBuilder();

builder.Services.AddSingleton<IResourceManager>(sp =>
    new PostgreSqlDriver("Host=..."));
builder.Services.AddSingleton<IResourceManager>(sp =>
    new PgVectorDriver("Host=..."));
builder.Services.AddSingleton<IResourceManager>(sp =>
    new PostgresBlobDriver("Host=..."));

Kernel kernel = builder.Build();

// 2. 每个 IResourceManager 被包装为 KernelPlugin，
//    其能力接口方法自动暴露为 KernelFunction
foreach (var rm in kernel.Services.GetServices<IResourceManager>())
{
    kernel.Plugins.AddFromObject(rm, rm.Name);
}

// 3. TransactionCoordinator 通过 Kernel 发现和调度 Driver
public sealed class TransactionCoordinator
{
    private readonly Kernel _kernel;

    public async Task<SqlQueryResult> QueryAsync(
        string transactionId,
        SqlQueryRequest request,
        CancellationToken ct)
    {
        // 查找提供 ISqlQueryCapability 的 Driver
        var sqlDriver = _kernel.Services
            .GetServices<IResourceManager>()
            .OfType<ISqlQueryCapability>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No SQL query driver registered");

        // Enlist 到当前事务（如果支持）
        if (sqlDriver is ITransactionalResourceManager txRm)
            txRm.Enlist(GetCurrentTransaction(transactionId));

        return await sqlDriver.QueryAsync(request, ct);
    }
}
```

**SK 技术栈在本项目中的使用要点：**

| SK 概念 | Adaptor 映射 |
|---------|-------------|
| `Kernel` | 中间件内部编排核心 |
| `KernelPlugin` | Driver 的包装单元 |
| `KernelFunction` | Driver 能力接口方法（预留 AI/自动化扩展点） |
| `KernelBuilder` / `IServiceCollection` | 中间件启动时组装 Driver |
| `IMemoryStore` (SK 内置) | 与 `IVectorSearchCapability` 可相互适配 |
| Plugin 自动发现 | 中间件通过 DI 容器自动发现已注册的 Driver |

### 5.6 Driver 事务能力声明

Driver 通过自身实现的接口组合来声明能力，同时保留显式枚举以支持运行时查询：

```csharp
[Flags]
public enum TransactionCapabilities
{
    None = 0,
    /// <summary>支持 IEnlistmentNotification（标准两阶段提交）</summary>
    TwoPhaseCommit = 1 << 0,
    /// <summary>支持可提升单阶段提交 PSPE</summary>
    Promotable = 1 << 1,
    /// <summary>支持补偿事务（Commit 失败后可回滚）</summary>
    Compensating = 1 << 2,
}

public static class ResourceManagerExtensions
{
    /// <summary>运行时查询 Driver 的事务能力</summary>
    public static TransactionCapabilities GetTransactionCapabilities(
        this IResourceManager rm)
    {
        var caps = TransactionCapabilities.None;
        if (rm is ITransactionalResourceManager) caps |= TransactionCapabilities.TwoPhaseCommit;
        // 通过 reflection 或已注册的元数据检查 PSPE/Compensating
        return caps;
    }
}
```

### 5.7 示例场景

#### 例 1: PgSQL + pgvector + BYTEA/LO（三者同库）

```
PostgreSqlDriver     : IResourceManager
                     + ITransactionalResourceManager  (PSPE 优化)
                     + ISqlExecuteCapability + ISqlQueryCapability
                     + IHealthCheckCapability

PgVectorDriver       : IResourceManager
                     + ITransactionalResourceManager  (共享连接, PSPE)
                     + IVectorUpsertCapability + IVectorSearchCapability
                     + IHealthCheckCapability

PostgresBlobDriver   : IResourceManager
                     + ITransactionalResourceManager  (共享连接, PSPE)
                     + IBlobUploadCapability + IBlobDownloadCapability
                     + IHealthCheckCapability
```

三者共享同一个 `NpgsqlConnection`，PSPE 使整个事务在 PG 本地完成，无需提升为分布式。

#### 例 2: MySQL + SQLite-Vector + AWS S3（完全异构）

```
MySqlDriver          : IResourceManager
                     + ITransactionalResourceManager  (2PC)
                     + ISqlExecuteCapability + ISqlQueryCapability

SqliteVectorDriver   : IResourceManager
                     + ITransactionalResourceManager  (2PC)
                     + IVectorUpsertCapability + IVectorSearchCapability

S3BlobDriver         : IResourceManager
                     + ITransactionalResourceManager  (应用层补偿)
                     + IBlobUploadCapability + IBlobDownloadCapability
                     + IHealthCheckCapability
```

三者分属不同存储系统，需完整 2PC。S3 在 `Prepare()` 中预上传并记录版本号，`Commit()` 确认，`Rollback()` 删除（补偿）。

---

## 6. 错误处理与重试

### 6.1 All or Nothing 策略

#### Phase 1 — Prepare（投票）

```
TransactionCoordinator.Commit():
  for each Driver:
    try:
      driver.Prepare(preparingEnlistment)
    catch:
      → ForceRollback for all Drivers
      → 全部进入 Rollback 流程

  全部通过:
    → 进入 Phase 2
```

#### Phase 2 — Commit（决定 + 重试）

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
          wait(backoff)  // 指数退避
          continue
        else:
          → 重试耗尽，进入 Rollback 流程

  全部成功:
    → 事务完成 (COMMITTED)

  任一失败 (重试耗尽):
    → Rollback for all Drivers (补偿)
    → 返回 COMMIT_STATUS_PARTIAL 及详细错误
```

### 6.2 配置参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `MaxRetryCount` | 3 | 每个 Driver Commit 阶段的最大重试次数 |
| `RetryBackoffBase` | 100ms | 指数退避基数 (`backoff * 2^retry`) |
| `DefaultTransactionTimeout` | 30s | 事务默认超时时间 |
| `SessionIdleTimeout` | 60s | 会话空闲超时（超时未操作自动回滚） |

### 6.3 回滚流程

```
TransactionCoordinator.Rollback():
  for each enlisted Driver:
    try:
      driver.Rollback(enlistment)
    catch:
      log_error("Driver {name} rollback failed, manual intervention required")

  清理事务状态
  通知 Consumer
```

> 注：Rollback 阶段的失败仅记录日志，不阻塞整体流程。此时需要人工介入处理不一致状态。

### 6.4 超时管理

超时由 `CommittableTransaction` 内建机制处理，Coordinator 不手写监控。

```
BeginTransaction(timeout)
  → 创建 CommittableTransaction 并设置超时

Timeout 触发:
  → .NET 自动调用所有 Enlisted Driver 的 Rollback()
  → 清理事务状态
  → (可选) 通知 Consumer（如 gRPC 连接仍存活）

注意:
  - CommittableTransaction 构造后超时不可修改
  - 如需长时间操作，Consumer 应创建超时无限的独立事务
  - SetTransactionTimeout RPC 已移除
```

---

## 7. 连接与会话管理

### 7.1 会话生命周期

```
┌───────────────────────────────────────┐
│  gRPC 连接建立                         │
│    ↓                                   │
│  Consumer: BeginTransaction            │
│    ↓                                   │
│  Session 创建 + Transaction 创建       │
│    ↓                                   │
│  Consumer: 数据操作 (多次)             │
│    → Driver Enlist (按需延迟)          │
│    ↓                                   │
│  Consumer: Commit / Rollback           │
│    ↓                                   │
│  Session 关闭 + Transaction 释放       │
│    ↓                                   │
│  gRPC 连接关闭                         │
└───────────────────────────────────────┘
```

### 7.2 并发与线程模型

- 每个事务在一个独立的 `Task` 中处理
- 多个 Consumer 可并行发起多个事务
- 同一事务内并发操作的串行化由 **Driver 层自行负责**，Coordinator 不做约束
  - Npgsql 等有状态驱动的 Driver 内部使用 per-transaction `SemaphoreSlim`
  - S3 等无状态驱动的 Driver 天然支持并发
- Coordinator 仅维护 `ConcurrentDictionary<string, TransactionEntry>` 事务表

### 7.3 驱逐策略

| 条件 | 行为 |
|------|------|
| 事务超时 | 自动 Rollback，清理状态 |
| gRPC 连接断开 | 关联的活跃事务自动 Rollback |
| 服务关闭（Graceful） | 等待进行中的 Commit 完成，Rollback 其余事务 |
| 服务崩溃 | 重启后加载持久化的事务状态（未来版本） |

---

## 8. 项目结构

### 8.1 单项目布局（当前阶段）

```
Adaptor/
├── Adaptor.csproj              # .NET 10 gRPC 服务项目
├── Program.cs                  # 入口：DI 配置、gRPC 服务注册
├── appsettings.json            # 配置文件
│
├── Protos/
│   └── adaptor.proto           # gRPC 协议定义
│
├── Services/
│   ├── AdaptorService.cs       # gRPC service 实现（endpoint group）
│   │                            # 注：不是 service 单元，只是 endpoint 路由
│   ├── TransactionCoordinator.cs # 事务协调器
│   └── SessionManager.cs       # 会话管理
│
├── Abstractions/
│   ├── IResourceManager.cs         # 基础 RM 标记接口
│   ├── ITransactionalResourceManager.cs  # 事务性 RM（含 Enlist）
│   ├── ISqlExecuteCapability.cs    # SQL 执行能力
│   ├── ISqlQueryCapability.cs      # SQL 查询能力
│   ├── IVectorUpsertCapability.cs  # 向量写入能力
│   ├── IVectorSearchCapability.cs  # 向量搜索能力
│   ├── IBlobUploadCapability.cs    # BLOB 上传能力
│   ├── IBlobDownloadCapability.cs  # BLOB 下载能力
│   └── IHealthCheckCapability.cs   # 健康检查能力
│
├── Drivers/
│   ├── Sql/
│   │   ├── PostgreSqlDriver.cs     # PostgreSQL + pgvector (SQL 部分)
│   │   └── MySqlDriver.cs
│   ├── Vector/
│   │   ├── PgVectorDriver.cs       # pgvector (Vector 部分)
│   │   └── SqliteVectorDriver.cs
│   └── Blob/
│       ├── PostgresBlobDriver.cs   # BYTEA + Large Object
│       └── S3BlobDriver.cs         # AWS S3 + 应用层补偿
│
├── Models/
│   └── DriverCommitResult.cs   # 提交结果记录（含 CommitStatus 枚举）
│
├── Configuration/
│   └── AdaptorOptions.cs       # 配置模型
│
└── Utilities/
    └── (暂空，后续按需添加)
```

### 8.2 多项目扩展（当需要自定义 Driver 接口时）

```
Adaptor.sln
├── src/
│   ├── Adaptor.Core/           # 核心：gRPC 服务、协调器、抽象接口
│   │   ├── Adaptor.Core.csproj
│   │   ├── Abstractions/
│   │   │   ├── IResourceManager.cs
│   │   │   ├── ITransactionalResourceManager.cs
│   │   │   ├── ISqlExecuteCapability.cs
│   │   │   ├── ISqlQueryCapability.cs
│   │   │   ├── IVectorUpsertCapability.cs
│   │   │   ├── IVectorSearchCapability.cs
│   │   │   ├── IBlobUploadCapability.cs
│   │   │   ├── IBlobDownloadCapability.cs
│   │   │   └── IHealthCheckCapability.cs
│   │   ├── Services/
│   │   │   ├── TransactionCoordinator.cs
│   │   │   └── AdaptorService.cs
│   │   └── Protos/
│   │       └── adaptor.proto
│   │
│   └── Adaptor.Drivers/        # 所有 Driver 实现（按需拆分）
│       ├── Adaptor.Drivers.csproj
│       ├── Sql/
│       ├── Vector/
│       └── Blob/
│
└── tests/
    └── Adaptor.Tests/
```

---

## 9. 尚未解决的问题

以下问题需在后续阶段确定：

1. **具体 API 需求** — gRPC 数据操作 RPC 的请求/响应消息结构，需在 API 需求冻结后设计
2. **身份认证与授权** — 是否需要在 gRPC 层面加入认证（mTLS / Token）？
3. **日志与可观测性** — OpenTelemetry 集成策略（Traces / Metrics / Logs）
4. **配置管理** — 如何管理 Driver 连接字符串？环境变量？配置中心？
5. **事务状态持久化** — 服务崩溃后如何恢复正在进行中的事务？
6. **Driver 热加载** — 是否支持运行时动态加载/卸载 Driver？
7. **Schema 管理** — SQL 表结构 / 向量索引 / BLOB 容器的自动初始化？
8. **多租户** — 是否需要支持？
9. **版本兼容性** — gRPC proto 的版本演进策略。
10. **测试策略** — 如何编写集成测试覆盖异构存储组合？
