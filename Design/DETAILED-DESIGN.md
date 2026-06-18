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
│  ┌──────────────┬──────────────┬──────────────────────┐   │
│  │  SQL Driver   │  Vector Driver│  BLOB Driver         │   │
│  │  Manager      │  Manager      │  Manager             │   │
│  ├──────────────┤├──────────────┤├──────────────────────┤   │
│  │ IStorageDriver<TSqlTx>        │ IStorageDriver<TBlobTx>   │
│  │ : IEnlistmentNotification     │ : IEnlistmentNotification │
│  └──────┬───────┴──────┬───────┴──────────┬───────────┘   │
│         │              │                  │                │
│  ┌──────▼──────┐ ┌─────▼──────┐  ┌────────▼────────┐     │
│  │ PgSQL       │ │ pgvector   │  │ BYTEA + LO      │     │
│  │ MySQL       │ │ SQLite-Vec │  │ AWS S3 + 补偿   │     │
│  │ ...         │ │ ...        │  │ ...             │     │
│  └─────────────┘ └────────────┘  └─────────────────┘     │
└──────────────────────────────────────────────────────────┘
```

### 2.2 关键概念

| 概念 | 定义 |
|------|------|
| **Transaction** | .NET `System.Transactions.Transaction` 实例，代表一个工作单元。中间件内部使用 `CommittableTransaction` 作为顶层事务 |
| **Enlistment** | Driver 将自身（及关联的本地事务）注册到 .NET Transaction 的过程 |
| **Session** | Consumer 与中间件之间的 gRPC 会话上下文，绑定到一个活跃事务 |
| **Driver** | 实现对特定存储引擎的适配器，提供事务能力声明和数据操作 |

### 2.3 事务生命周期

```
Consumer              Middleware              Driver 1 (SQL)      Driver 2 (Vector)    Driver 3 (BLOB)
   │                      │                       │                   │                   │
   │── BeginTransaction ──│                       │                   │                   │
   │                      │── 创建 .NET CommittableTransaction        │                   │
   │                      │── 为每个配置的 Driver 准备事务上下文       │                   │
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
  rpc GetTransactionStatus(GetTransactionStatusRequest) returns (GetTransactionStatusResponse);
  rpc SetTransactionTimeout(SetTransactionTimeoutRequest) returns (SetTransactionTimeoutResponse);
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

> **注意**：上述 RPC 列表是框架性的。具体的请求/响应消息结构需在 API 需求冻结后设计。

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
  google.protobuf.Timestamp expires_at = 2;
  repeated string enlisted_drivers = 3;
}

enum TransactionState {
  TRANSACTION_STATE_UNSPECIFIED = 0;
  TRANSACTION_STATE_ACTIVE = 1;
  TRANSACTION_STATE_PREPARING = 2;
  TRANSACTION_STATE_PREPARED = 3;
  TRANSACTION_STATE_COMMITTING = 4;
  TRANSACTION_STATE_COMMITTED = 5;
  TRANSACTION_STATE_ROLLING_BACK = 6;
  TRANSACTION_STATE_ROLLED_BACK = 7;
  TRANSACTION_STATE_TIMEOUT = 8;
}

// ─── 超时设置 ───
message SetTransactionTimeoutRequest {
  string transaction_id = 1;
  google.protobuf.Duration timeout = 2;
}

message SetTransactionTimeoutResponse {
  bool success = 1;
}
```

### 3.5 连接与 KeepAlive

- 使用 gRPC 内置的 HTTP/2 keepalive ping
- Consumer 可通过 `SetTransactionTimeout` 控制事务级别的超时
- 中间件服务端配置默认事务超时 + 会话空闲超时
- 超时未 commit 的事务自动回滚

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

### 4.4 事务状态机

```
                         ┌─────────┐
                         │  ACTIVE  │ ◀── BeginTransaction
                         └────┬─────┘
                              │
               ┌──────────────┼──────────────┐
               │              │              │
          Data Op      SetTimeout       Commit / Rollback
               │              │              │
               ▼              ▼              ▼
          (Enlist)      (更新超时)     ┌──────────┐
                         │           │ PREPARING │── Commit
                         ▼           └─────┬─────┘
                    (继续 ACTIVE)           │
                                    ┌──────▼──────┐
                               ┌────┤   PREPARED   ├────┐
                               │    └──────┬──────┘    │
                               ▼           ▼           ▼
                         ┌──────────┐ ┌──────────┐ ┌──────────┐
                         │COMMITTING│ │ROLLBACK  │ │ROLLBACK  │
                         │          │ │(Prepare  │ │(Timeout  │
                         │          │ │ 失败)    │ │ 触发)    │
                         └────┬─────┘ └────┬─────┘ └────┬─────┘
                              │            │            │
                         ┌────▼────┐  ┌────▼────┐  ┌────▼────┐
                         │COMMITTED│  │ROLLED   │  │ROLLED   │
                         │         │  │BACK     │  │BACK     │
                         └─────────┘  └─────────┘  └─────────┘

               所有终态不可逆。TIMEOUT 状态在超时时进入，
               自动触发回滚。COMMITTED / ROLLED_BACK / TIMEOUT
               为终态。
```

---

## 5. Driver 接口设计

### 5.1 设计原则

- Driver 利用 .NET 现有的分布式事务接口（`IEnlistmentNotification` 等）
- Driver 不需要实现自定义事务接口，除非 .NET 内置设施不足
- 每个 Driver 类型面向一种存储引擎
- Driver 通过依赖注入（DI）注册到中间件

### 5.2 Driver 能力声明

```csharp
[Flags]
public enum StorageCapabilities
{
    None = 0,
    /// <summary>支持单阶段提交 (IEnlistmentNotification)</summary>
    SinglePhase = 1 << 0,
    /// <summary>支持可提升单阶段提交 (IPromotableSinglePhaseNotification)</summary>
    Promotable = 1 << 1,
    /// <summary>支持原生分布式事务 (如 XA)</summary>
    Distributed = 1 << 2,
    /// <summary>支持补偿事务（用于 All or Nothing 回滚）</summary>
    Compensating = 1 << 3,
}
```

### 5.3 Driver 核心约定

Driver 通过实现 `IEnlistmentNotification`（系统.Transactions 命名空间）表明自己支持两阶段提交。这是一个 .NET 标准接口，不是自定义接口。

```csharp
public interface IStorageDriver : IEnlistmentNotification
{
    /// <summary>Driver 名称，用于日志和错误报告</summary>
    string Name { get; }

    /// <summary>存储类型</summary>
    StorageType StorageType { get; }

    /// <summary>能力声明</summary>
    StorageCapabilities Capabilities { get; }

    /// <summary>
    /// 将当前 Driver 注册到指定的 .NET 事务中。
    /// 该方法在第一次数据操作时由 TransactionCoordinator 调用。
    /// </summary>
    void Enlist(Transaction transaction);

    /// <summary>
    /// 检查 Driver 连接状态。
    /// </summary>
    bool HealthCheck();
}

public enum StorageType
{
    Sql,
    Vector,
    Blob,
}
```

### 5.4 IEnlistmentNotification 回调映射

```csharp
public class MySqlDriver : IStorageDriver
{
    // ─── IEnlistmentNotification ───

    /// <summary>
    /// Phase 1: Prepare。系统.Transactions 在提交时自动调用。
    /// 在此方法中验证本地事务是否可以提交。
    /// </summary>
    void IEnlistmentNotification.Prepare(PreparingEnlistment preparingEnlistment)
    {
        try
        {
            // 验证本地事务状态
            // 如果一切正常:
            preparingEnlistment.Prepared();
            // 如果失败:
            // preparingEnlistment.ForceRollback(error);
        }
        catch (Exception ex)
        {
            preparingEnlistment.ForceRollback(ex);
        }
    }

    /// <summary>
    /// Phase 2: Commit。所有 Driver 都 Prepared 后调用。
    /// </summary>
    void IEnlistmentNotification.Commit(Enlistment enlistment)
    {
        // 提交本地事务
        // 通知中间件完成
        enlistment.Done();
    }

    /// <summary>
    /// Rollback。任一阶段失败或主动回滚时调用。
    /// </summary>
    void IEnlistmentNotification.Rollback(Enlistment enlistment)
    {
        // 回滚本地事务
        enlistment.Done();
    }

    /// <summary>
    /// InDoubt。事务状态不确定时调用（罕见）。
    /// </summary>
    void IEnlistmentNotification.InDoubt(Enlistment enlistment)
    {
        // 记录不确定状态，人工介入
        enlistment.Done();
    }
}
```

### 5.5 Driver 实现示例

#### 例 1: PgSQL (SQL) + pgvector (Vector) + BYTEA/LO (BLOB)

三者全部在同一个 PostgreSQL 实例中。这种情况下：

- **优化策略**：三个 Driver 共享同一个 `NpgsqlConnection`
- **PSPE**：使用 `IPromotableSinglePhaseNotification`，初始为本地 PG 事务，仅在需要跨 PG 实例时提升为分布式
- 实际场景中如果三者同库，单数据库事务即可覆盖所有操作

#### 例 2: MySQL (SQL) + SQLite-Vector (Vector) + AWS S3 (BLOB)

三者分属不同存储系统，需要完整的 2PC：

- **MySQL Driver**：通过 `IEnlistmentNotification` 实现 2PC
- **SQLite-Vector Driver**：通过 `IEnlistmentNotification` 实现 2PC
- **AWS S3 Driver**：需要应用层补偿事务 — 在 `Prepare()` 中预上传并记录版本号，`Commit()` 中确认，`Rollback()` 中删除（补偿）

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

```
BeginTransaction
  → 启动 .NET CommittableTransaction 并设置超时
  → 启动后台 CancellationTokenSource 关联

Timeout 触发:
  → .NET 自动调用所有 Enlisted Driver 的 Rollback()
  → 清理事务状态
  → (可选) 通知 Consumer（如 gRPC 连接仍存活）

Consumer 调用 SetTimeout:
  → 更新 CancellationTokenSource
  → 不能超过服务端限制的最大超时
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
- 同一事务内的操作顺序执行（通过 `SemaphoreSlim` 或 `AsyncLocal` 协调）
- 事务表 `ConcurrentDictionary<string, TransactionState>` 管理全局事务状态

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
│   ├── IStorageDriver.cs       # Driver 接口
│   └── StorageCapabilities.cs  # 能力声明
│
├── Drivers/
│   ├── Sql/
│   │   ├── PostgreSqlDriver.cs # PostgreSQL + pgvector (SQL 部分)
│   │   └── MySqlDriver.cs
│   ├── Vector/
│   │   ├── PgVectorDriver.cs   # pgvector (Vector 部分)
│   │   └── SqliteVectorDriver.cs
│   └── Blob/
│       ├── PostgresBlobDriver.cs # BYTEA + Large Object
│       └── S3BlobDriver.cs       # AWS S3
│
├── Models/
│   ├── TransactionState.cs     # 事务状态模型
│   └── DriverCommitResult.cs   # 提交结果记录
│
├── Configuration/
│   └── AdaptorOptions.cs       # 配置模型
│
└── Utilities/
    ├── RetryPolicy.cs           # 重试策略（指数退避）
    └── TimeoutManager.cs        # 超时管理
```

### 8.2 多项目扩展（当需要自定义 Driver 接口时）

```
Adaptor.sln
├── src/
│   ├── Adaptor.Core/           # 核心：gRPC 服务、协调器、抽象接口
│   │   ├── Adaptor.Core.csproj
│   │   ├── Abstractions/
│   │   │   └── IStorageDriver.cs
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
