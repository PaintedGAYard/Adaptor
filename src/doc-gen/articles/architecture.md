# Architecture Overview

## Layered Design

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
                           │ gRPC
┌──────────────────────────▼───────────────────────────────┐
│  Adaptor Service Layer                                    │
│  (.NET 10 gRPC 服务)                                      │
│                                                            │
│  ┌────────────────────────────────────────────────────┐   │
│  │  TransactionCoordinator                             │   │
│  │  ┌──────────────────────────────────────────────┐  │   │
│  │  │  System.Transactions Abstraction              │  │   │
│  │  │  - CommittableTransaction                     │  │   │
│  │  │  - TransactionScope                            │  │   │
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
│  │  IResourceManager              │ IResourceManager          │
└──────────────────────────┬───────────────────────────────┘
                           │ Driver SPI
┌──────────────────────────▼───────────────────────────────┐
│  Driver Layer                                             │
│  (PostgreSQL, future: MySQL, Azure Blob, ...)             │
└──────────────────────────────────────────────────────────┘
```

## Core Abstractions

### `IResourceManager`
Base interface for all storage drivers. Provides identity (`Name`, `ResourceType`, `ResourceManagerIdentifier`) and serves as the common contract.

### `ITransactionalResourceManager`
Extends `IResourceManager` for drivers that participate in distributed transaction coordination via `System.Transactions`. Drivers call `Transaction.EnlistVolatile` or `Transaction.EnlistDurable` to register.

### Capability Interfaces
Orthogonal interfaces that drivers implement to expose functionality:

- `IRelationalExecuteCapability` — Execute SQL commands
- `IRelationalQueryCapability` — Query relational data
- `IRelationalVectorSearchCapability` — Vector similarity search
- `IBlobUploadCapability` — Upload BLOB data
- `IBlobDownloadCapability` — Download BLOB data
- `IBlobRandomAccessCapability` — Random access to BLOB data
- `IHealthCheckCapability` — Health check endpoint

## Transaction Flow

1. **Begin** — Consumer requests a new transaction via gRPC
2. **Enlist** — Drivers implementing `ITransactionalResourceManager` enlist in the .NET `Transaction`
3. **Operation** — Consumer performs SQL/Vector/BLOB operations within the transaction scope
4. **Commit/Rollback** — Consumer issues commit or rollback; `TransactionCoordinator` orchestrates the two-phase commit protocol
5. **Completion** — All resources are finalized
