# Blob Stream Access Capability — 详细设计

> **版本**: 1.0  
> **日期**: 2026-06-18  
> **状态**: Draft

---

## 1. 架构概览

### 1.1 Control/Data Plane 分离

```
┌──────────────────────────────────────────────────────────────────┐
│  Consumer (Python / JS / Go)                                      │
│                                                                    │
│  gRPC:                             WebSocket:                      │
│  ─────────                         ───────────                     │
│  tx = client.BeginTransaction()    ws = connect("wss://host/blob-stream")│
│  ...                                ws.send(Open(tx, key))         │
│  client.CommitTransaction(tx)      ws.send(Seek(handle, 0))       │
│  client.RollbackTransaction(tx)    ws.send(Read(handle, 1024))    │
│                                    ws.send(Close(handle))          │
└────────────────────────────────┬─────────────────────────────────┘
                                 │
         gRPC (50051) ──────────┼────────── WebSocket (同端口)
                                 │
┌────────────────────────────────▼─────────────────────────────────┐
│  Adaptor Service (.NET 10 / Kestrel)                              │
│                                                                    │
│  ┌─────────────────────┐   ┌──────────────────────────────────┐   │
│  │  gRPC Services       │   │  BlobStream WebSocket Handler    │   │
│  │  (Control Plane)     │   │  (Data Plane)                    │   │
│  │                       │   │                                   │   │
│  │  TransactionService   │   │  ConnectionHandler               │   │
│  │  RelationalService    │   │  ├─ OpCode 0x01 = Open           │   │
│  │  VectorService        │   │  ├─ OpCode 0x02 = Close          │   │
│  │  BlobService (unary)  │   │  ├─ OpCode 0x03 = Read           │   │
│  │                       │   │  ├─ OpCode 0x04 = Write          │   │
│  │  ─ 共用 DI 容器 ───── │   │  ├─ OpCode 0x05 = Seek          │   │
│  │                       │   │  ├─ OpCode 0x06 = Truncate       │   │
│  │  TransactionCoordinator│  │  └─ OpCode 0xFF = Error          │   │
│  │  HandleManager         │  └───────────┬──────────────────────┘   │
│  └──────────┬──────────────┘             │                          │
│             │                             │                          │
│             └─────────┬───────────────────┘                          │
│                       │                                              │
│  ┌────────────────────▼────────────────────────────────────────┐    │
│  │  Driver Layer (同 DI 容器的 IResourceManager 实例)            │    │
│  │                                                               │    │
│  │  PostgresBlobDriver :                                          │    │
│  │    IResourceManager                                            │    │
│  │    ITransactionalResourceManager                               │    │
│  │    IBlobUploadCapability          (unary upload, 已有)          │    │
│  │    IBlobDownloadCapability        (unary download, 已有)        │    │
│  │    IBlobRandomAccessCapability    (stream random access, 新增)  │    │
│  │    IHealthCheckCapability                                     │    │
│  └───────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────┘
```

### 1.2 设计原则

| 原则 | 说明 |
|------|------|
| **Control/Data Plane 分离** | gRPC 负责事务生命周期管理；WebSocket 负责 Blob 数据流读写 |
| **tx_id 作为桥接 Token** | WebSocket 协议中的每条消息携带 tx_id，用于关联到 gRPC 创建的事务 |
| **Handle 抽象** | 服务端维护 handle → (LO fd, Transaction, Connection) 映射，客户端通过 handle 操作 |
| **Per-handle 串行化** | 每个 handle 一把锁，保证 Seek+Read/Write 的原子性 |
| **自动资源回收** | Handle 超时 + 连接断开双重 GC，防止 LO fd 泄漏 |

---

## 2. gRPC 控制平面

### 2.1 保留的 gRPC 服务

| 服务 | 保留 | 说明 |
|------|:----:|------|
| `Transaction` | ✅ | Begin/Commit/Rollback/GetStatus |
| `DBRelational` | ✅ | Execute/Query/ExecuteBatch |
| `DBRelationalVector` | ✅ | Search |
| `DBBLOB` | ✅ | BlobUpload/BlobDownload/BlobDelete/BlobList（unary 操作保留） |

**变更**: 无。gRPC 侧不需要任何修改。

---

## 3. WebSocket 数据平面

### 3.1 端点

```
wss://host:50051/blob-stream   (共享 Kestrel 端口)
```

WebSocket 升级路径在 ASP.NET Core 中间件管道中注册，与 gRPC 共享同一端口和 TLS。

### 3.2 二进制协议

采用 **简单 TLV (Type-Length-Value)** 格式：

```
┌─────────────────────────────────────────────────────────────┐
│  请求消息格式:                                                │
│  ┌──────┬────────┬──────────┬──────────────────────────┐    │
│  │ 1B   │ 4B     │ 4B       │ N-Bytes                  │    │
│  │ Ver  │ OpCode │ BodyLen  │ Body (opcode-specific)   │    │
│  │ =0x01│ (uint) │ (uint BE)│                          │    │
│  └──────┴────────┴──────────┴──────────────────────────┘    │
│                                                              │
│  响应消息格式:                                                │
│  ┌──────┬────────┬──────────┬──────────────────────────┐    │
│  │ 1B   │ 4B     │ 4B       │ N-Bytes                  │    │
│  │ Ver  │ OpCode │ BodyLen  │ Body (opcode-specific)   │    │
│  │ =0x01│ (=请求 │ (uint BE)│ 或 ErrorBody if OpCode   │    │
│  │      │  OpCode│          │ = 0xFF)                  │    │
│  └──────┴────────┴──────────┴──────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

所有多字节整数均为 **大端序 (Big Endian)**。

### 3.3 OpCode 定义

| OpCode | 方向 | 名称 | 请求 Body | 响应 Body |
|:------:|:---:|------|-----------|-----------|
| `0x01` | C→S | **Open** | `[4B txIdLen][UTF8 txId][4B keyLen][UTF8 key][1B mode]` | `[8B handle]` |
| `0x02` | C→S | **Close** | `[8B handle]` | `[1B status]` |
| `0x03` | C→S | **Read** | `[8B handle][4B count]` | `[4B dataLen][data]` |
| `0x04` | C→S | **Write** | `[8B handle][4B dataLen][data]` | `[4B bytesWritten]` |
| `0x05` | C→S | **Seek** | `[8B handle][8B offset][1B whence(0=Begin,1=Cur,2=End)]` | `[8B newPosition]` |
| `0x06` | C→S | **Truncate** | `[8B handle][8B newLength]` | `[1B status]` |
| `0xFF` | S→C | **Error** | — | `[4B errorCode][2B msgLen][UTF8 msg]` |

### 3.4 Open Mode 枚举

| Mode 值 | 名称 | 语义 |
|:-------:|------|------|
| `0x01` | Read | 打开已有 blob 进行读取 |
| `0x02` | Write | 打开已有 blob 进行写入（从 offset 0 开始） |
| `0x03` | ReadWrite | 打开已有 blob 进行读写 |
| `0x04` | Create | 创建新 blob，key 已存在则报错 |
| `0x05` | CreateOrReplace | 创建新 blob，key 存在则替换（Create-New-Swap） |
| `0x06` | Append | 打开已有 blob 并在末尾追加 |

### 3.5 Error Code 枚举

| Code | 名称 | 说明 |
|:----:|------|------|
| `0x0001` | InvalidOpCode | 无法识别的 OpCode |
| `0x0002` | InvalidBody | Body 格式错误或长度不匹配 |
| `0x0003` | InvalidHandle | Handle 不存在或已关闭 |
| `0x0004` | TransactionNotFound | tx_id 不存在 |
| `0x0005` | TransactionNotActive | 事务已提交/回滚 |
| `0x0006` | KeyNotFound | Blob key 不存在（Open Read 时） |
| `0x0007` | KeyAlreadyExists | Blob key 已存在（Open Create 时） |
| `0x0008` | DriverNotAvailable | 未注册支持随机访问的 Driver |
| `0x0009` | IoError | 底层存储 I/O 错误 |
| `0x000A` | HandleLimitExceeded | 超过最大 handle 数限制 |
| `0x000B` | ReadBeyondEnd | 读取超出 blob 末尾 |
| `0xFFFF` | UnknownError | 其他内部错误 |

---

## 4. 服务端组件设计

### 4.1 组件依赖关系

```
BlobStreamConnectionHandler (WebSocket)
  ├── TransactionCoordinator  (查找事务、执行能力)
  ├── HandleManager           (handle 生命周期管理)
  │     ├── HandleEntry[]     (per-handle 状态)
  │     └── Timer             (超时 GC)
  └── ILogger
```

### 4.2 HandleManager

**职责**:
1. 分配全局唯一的 handle ID（`Interlocked.Increment`，起始于随机值）
2. 维护 `ConcurrentDictionary<long, HandleEntry>` 映射
3. 定时清理过期 handle（事务已结束 / 空闲超时）
4. 限制每连接最大 handle 数（默认 64）

```csharp
public sealed class HandleManager : IDisposable
{
    private long _nextHandle;  // 从随机值开始
    private readonly ConcurrentDictionary<long, HandleEntry> _handles = new();
    
    public long Register(HandleEntry entry);
    public bool TryGet(long handle, out HandleEntry entry);
    public bool Remove(long handle, out HandleEntry entry);
    public IReadOnlyList<HandleEntry> RemoveAllForConnection(string connectionId);
    public int ActiveHandleCount { get; }
}
```

### 4.3 HandleEntry

```csharp
internal sealed class HandleEntry : IDisposable
{
    public long HandleId { get; init; }
    public int LoFd { get; set; }           // PostgreSQL LO file descriptor
    public string Key { get; init; } = "";
    public string TransactionId { get; init; } = "";
    public string ConnectionId { get; init; } = "";
    public DateTime LastActivityAt { get; set; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    
    // 实际资源由 Driver 管理，HandleEntry 只记录映射
}
```

### 4.4 BlobStreamConnectionHandler

**消息处理流程**:

```
Receive binary message
  → 解析固定头部 (9 bytes)
  → 解析 OpCode-specific Body
  → 验证 tx_id (若需要)
  → 获取 HandleEntry (若需要)
  → 获取 Transaction (via Coordinator)
  → acquire HandleEntry.Gate (若需要)
  → 调用 Driver 执行操作
  → 构造响应消息
  → Send binary response
  → release HandleEntry.Gate
```

**连接关闭处理**:
```
WebSocket 断开
  → HandleManager.RemoveAllForConnection(connectionId)
  → 对每个 handle: CloseBlobAsync(fd, tx) → lo_close
```

---

## 5. Coordinator 能力接口

### 5.1 IBlobRandomAccessCapability

```csharp
public enum BlobAccessMode
{
    Read = 1,
    Write = 2,
    ReadWrite = 3,
    Create = 4,
    CreateOrReplace = 5,
    Append = 6,
}

public interface IBlobRandomAccessCapability
{
    Task<BlobOpenResult> OpenAsync(
        string key, BlobAccessMode mode, Transaction transaction, CancellationToken ct);

    Task CloseAsync(
        int loFd, Transaction transaction, CancellationToken ct);

    Task<BlobReadResult> ReadAsync(
        int loFd, int count, Transaction transaction, CancellationToken ct);

    Task<int> WriteAsync(
        int loFd, byte[] data, Transaction transaction, CancellationToken ct);

    Task<long> SeekAsync(
        int loFd, long offset, SeekOrigin origin, Transaction transaction, CancellationToken ct);

    Task TruncateAsync(
        int loFd, long length, Transaction transaction, CancellationToken ct);
}
```

### 5.2 模型

```csharp
public sealed record BlobOpenResult(
    int LoFd,           // PostgreSQL Large Object file descriptor
    long BlobSize,      // 已有 blob 的大小（新建时为 0）
    string? ErrorMessage = null);

public sealed record BlobReadResult(
    byte[] Data,
    int BytesRead,
    string? ErrorMessage = null);
```

---

## 6. Driver 实现（PostgresBlobDriver 扩展）

### 6.1 新增方法

| 方法 | PostgreSQL 实现 |
|------|----------------|
| `OpenAsync(key, mode, tx)` | 1. 通过 tx 查找连接<br>2. 查询 `adaptor_blob_store` 获取 OID<br>3. `lo_open(oid, mode)` → fd<br>4. Return (fd, size) |
| `CloseAsync(fd, tx)` | `lo_close(fd)` |
| `ReadAsync(fd, count, tx)` | `lo_read(fd, count)` → byte[] |
| `WriteAsync(fd, data, tx)` | `lo_write(fd, data)` → bytes written |
| `SeekAsync(fd, offset, origin, tx)` | `lo_lseek(fd, offset, whence)` → new position |
| `TruncateAsync(fd, length, tx)` | `lo_truncate(fd, length)` |

### 6.2 Create 模式特殊处理

```
Open(Create) 或 Open(CreateOrReplace):
  1. lo_creat(-1) → 新 OID
  2. 写入映射表 INSERT ... ON CONFLICT DO ...
  3. lo_open(newOid, INV_WRITE) → fd
  4. Return (fd, 0)

Open(CreateOrReplace) 时 key 已存在:
  1. 查询旧 OID
  2. lo_creat(-1) → 新 OID
  3. UPDATE adaptor_blob_store SET oid = @newOid
  4. lo_unlink(@oldOid)
  5. lo_open(newOid, INV_WRITE) → fd
```

---

## 7. 关键流程示例

### 7.1 大文件上传（流式覆盖写入）

```
Consumer                              Adaptor Server
─────────────────────────────────────────────────────────
(gRPC) tx = BeginTransaction()
  │
(WS)   Open(tx, "video.mp4", CreateOrReplace)
  │                                     验证 tx Active
  │                                     lo_creat → OID=123
  │                                     lo_open(123, INV_WRITE) → fd=5
  │                                     HandleManager.Register(fd=5) → handle=42
  │←── handle=42
  │
(WS)   Seek(42, 0)
  │                                     lo_lseek(5, 0, SEEK_SET)
  │←── position=0
  │
(WS)   Write(42, [chunk1])   ← 客户端循环
  │                                     lo_write(5, chunk1) → 65536
  │←── bytesWritten=65536
  │
(WS)   Write(42, [chunkN])
  │                                     lo_write(5, chunkN) → 32768
  │←── bytesWritten=32768
  │
(WS)   Close(42)
  │                                     lo_close(5)
  │                                     INSERT adaptor_blob_store ...
  │←── OK
  │
(gRPC) CommitTransaction(tx)
  │                                     2PC → lo 事务提交
```

### 7.2 随机读取

```
Consumer                              Adaptor Server
─────────────────────────────────────────────────────────
(gRPC) tx = BeginTransaction()
  │
(WS)   Open(tx, "archive.bin", Read)
  │←── handle=7, blobSize=104857600
  │
(WS)   Seek(7, 4096)
  │←── position=4096
  │
(WS)   Read(7, 1024)
  │                                     lo_read(5, 1024) → data
  │←── data[1024]
  │
(WS)   Close(7)
  │←── OK
  │
(gRPC) RollbackTransaction(tx)   ← 只读操作，无需提交
```

---

## 8. 文件清单

| 操作 | 文件 | 说明 |
|:----:|------|------|
| **新增** | `Coordinator/Abstractions/IBlobRandomAccessCapability.cs` | 随机访问能力接口 |
| **新增** | `Coordinator/Models/BlobStreamModels.cs` | BlobOpenResult / BlobReadResult |
| **新增** | `Service/BlobStream/Protocol/OpCode.cs` | OpCode 枚举 |
| **新增** | `Service/BlobStream/Protocol/BlobStreamMessage.cs` | 二进制消息读写 |
| **新增** | `Service/BlobStream/Protocol/BlobStreamException.cs` | 错误码 + 异常 |
| **新增** | `Service/BlobStream/HandleManager.cs` | Handle 生命周期管理 |
| **新增** | `Service/BlobStream/HandleEntry.cs` | Handle 状态记录 |
| **新增** | `Service/BlobStream/BlobStreamConnectionHandler.cs` | WebSocket 连接处理器 |
| **修改** | `Driver/Postgre/PostgresBlobDriver.cs` | 实现 IBlobRandomAccessCapability |
| **修改** | `Service/Program.cs` | 注册 WebSocket + BlobStream 组件 |

---

## 9. 实施反思与修复记录

### 9.1 已识别并修复的坑

| # | 风险 | 等级 | 修复措施 |
|:-:|------|:----:|---------|
| 1 | **大 Read 导致 OOM** — 客户端请求 `int.MaxValue` 字节读取 | 🟡 | 添加 `MaxReadSize = 4 MiB` 限制，超限返回 `InvalidBody` 错误 |
| 2 | **Open 时 ExecuteOnCapabilityAsync 异常映射** — `InvalidOperationException` 被吞为 `UnknownError` | 🟡 | 添加 catch 子句，将事务不存在 / Driver 不存在映射到对应协议错误码 |
| 3 | **GetValidatedEntry 不清理已结束事务的 handle** — TransactionNotActive 时 handle 残留 | 🟡 | 两个分支均添加 `_handleManager.Remove` + `entry.Dispose()` |
| 4 | **事务完成后 handle 无通知** — BlobEnlistmentHandler 清理连接后 handle 仍在 HandleManager 中 | 🟡 | 添加 `InvalidateHandlesForTransaction(txId)` 方法；GetValidatedEntry 即时检测 + GC 定时器兜底 |

### 9.2 已识别但未修复的坑（需要后续版本处理）

| # | 风险 | 等级 | 说明 |
|:-:|------|:----:|------|
| 5 | **PG lo_lseek 32 位 offset** — 大于 2GB 的文件无法正确定位 | 🔴 | PostgreSQL `lo_lseek` 的 `offset` 参数是 `int`(32-bit)。PG 14+ 提供 `lo_lseek64` 接受 `bigint`。当前实现传递 `long`，在 PG 14+ 上自动使用 `lo_lseek64`。需要在部署文档中标注 PG 版本要求 |
| 6 | **PG lo_write 的 bytea 上限** — 单次写入超过 1GB 时 PG 报错 | 🟡 | WebSocket 协议已限制单条消息 ≤ 32MB；大文件写入由客户端分块，不会触发此限制 |
| 7 | **ReceiveFullMessageAsync 双倍拷贝** — 先租 ArrayPool 再复制到新 byte[] | 🟡 | 当前实现正确但非零拷贝。优化方案：直接使用 `ArrayPool` 租用缓冲区并在解析前保持引用，复杂度高且收益有限，暂不处理 |
| 8 | **WebSocket 消息缓冲区指数增长** — 大消息时可能导致碎片 | 🟡 | `MaxMessageSize = 32MB` 限制了最大分配量，32MB 以下的内存增长可接受 |
| 9 | **HandleManager 单点** — 进程重启后所有 handle 失效 | 🟡 | Handle 是进程内状态，与事务生命周期一致（事务也在进程中）。进程重启 → 所有事务失效 → handle 自动无效。这是预期行为，不是 bug |
| 10 | **HandleManager GC 与请求处理器的并发** — Timer 回调与请求处理器同时访问 `_handles` | 🟢 | `ConcurrentDictionary` 线程安全；Remove 和 TryGet 是原子操作。GC 可能移除一个正在被请求使用的 handle，但请求处理器会通过 `GetValidatedEntry` 发现 handle 已不存在，返回 `InvalidHandle` 错误给客户端 |

### 9.3 架构层面的潜在问题

| 问题 | 分析 |
|------|------|
| **事务生命周期 vs Handle 生命周期耦合** | Handle 绑定到 .NET `Transaction`，而 transaction 的生命周期由 gRPC 控制（gRPC client 调用 Commit）。这意味着 WebSocket 的 Handle 存活期依赖于 gRPC 连接的状态。若 gRPC client 崩溃但 WebSocket 连接还在，handle 会因 GC 超时（5 分钟）而回收，但在此期间 LO fd 保持打开。这是合理的设计取舍 |
| **Driver 的 Enlist 语义** | `IBlobRandomAccessCapability.OpenAsync` 内部通过 `GetEntry(transaction)` 查找连接，如果未 enlist 会抛出异常。因此 WebSocket handler 必须在 Open 之前确保 driver 已 enlist。当前的 `ExecuteOnCapabilityAsync` 自动处理了 enlistment（如果 driver 实现 `ITransactionalResourceManager`），所以 Open 时已自动 Enlist |
| **负载均衡** | Handle 是进程内状态，无法在多实例间共享。需要 **sticky session** 或将 WebSocket 连接路由到同一实例。这是 WebSocket 类协议的通用约束 |
| **PG 连接数** | 每个事务创建一个 PG 连接（通过 `Enlist`），该连接在事务生命周期内保持打开。每个 handle 通过这个已建立的连接操作 LO fd，不额外消耗连接数 |
