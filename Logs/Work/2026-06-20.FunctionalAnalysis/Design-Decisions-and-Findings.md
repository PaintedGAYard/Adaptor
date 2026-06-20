# Middleware Functional Analysis — Design Decisions & Findings

> **Session**: 2026-06-20  
> **Topic**: Middleware serviceability analysis, BlobStream protocol design review  
> **Participants**: User (initial designer), Agent (analysis)

---

## Part 1: Summary of analysis

### 1.1 Initial scope

The original goal was to identify what's still missing before the Adaptor middleware becomes serviceable — separating core business logic from advanced features and evaluating current implementation coverage.

### 1.2 Key outcomes

| Area | Outcome |
|------|---------|
| **Core functionality** | 75% serviceable. Transaction lifecycle, relational, vector, BLOB unary operations all work. |
| **BlobStream data plane** | Random-access position state is broken (defect 1, 🔴). Fix approach is straightforward. |
| **Design doc correction** | §6.1 described a manual per-driver 2PC loop, contradicting §4.2's correct ".NET 内部触发两阶段提交". Fixed. Dead config removed. |
| **Protocol design review** | Deep analysis of gRPC+WebSocket control/data plane separation led to revised BlobStream protocol model with reconnection support. |

### 1.3 Defects withdrawn

| Originally flagged | Verdict | Reason |
|--------------------|:-------:|--------|
| Coordinador missing per-driver 2PC retry | ❌ Not a defect | .NET `System.Transactions` handles 2PC internally. Design doc §6.1 was wrong, not the code. |
| gRPC connection disconnect should rollback | ❌ Not feasible | `ServerCallContext.CancellationToken` is not tied to TCP connection. gRPC keepalive is at HTTP handler level; server doesn't immediately detect drops. Transaction timeout is the correct safety net. |

---

## Part 2: Design decisions

### Decision 1: Control/Data Plane separation model

**Status: ✅ Confirmed**

The BlobStream protocol will follow a strict separation:
- **gRPC** owns transaction lifecycle: `BeginTransaction` / `CommitTransaction` / `RollbackTransaction` + `BeginSession` / `EndSession`
- **WebSocket** is pure data plane: `Open` / `Close` / `Read` / `Write` / `Seek` / `Truncate`
- **`OpCode.Commit` (0x07) will be removed** — all commits go through gRPC

### Decision 2: Session-based reconnection

**Status: ✅ Confirmed**

The session token issued by gRPC `BeginSession` serves as a reconnection anchor:
- WS disconnect (non-graceful) → transaction stays alive ("pause")
- WS reconnect with same token → resume existing transaction
- Server handshake indicates `reconnected: true/false`
- Consumer must re-establish all handles after reconnect

### Decision 3: Three disconnect semantics

**Status: ✅ Confirmed**

| State | Trigger | Transaction | Handles | Session token |
|-------|---------|:-----------:|:-------:|:------------:|
| **Pause** | WS network drop / process crash | Kept | Cleaned | Valid |
| **Abort** | Timeout (PausedTimeout or TxTimeout) | Rolled back | Released | Invalid |
| **Terminate** | gRPC `EndSession` / WS Close frame | Rolled back | Released | Invalid |

### Decision 4: 1:1:1 binding

**Status: ✅ Confirmed**

Strict one-to-one-to-one relationship: **1 Stream : 1 WebSocket : 1 Transaction**. All are created together and end together. A session token can have at most one active WebSocket connection at any time. The same token can reconnect (after previous WS dies), but concurrent connections are rejected.

### Decision 5: WS disconnect = pause (not abort)

**Status: ✅ Confirmed**

On non-graceful WebSocket disconnection:
1. `HandleManager.RemoveAllForConnection(connectionId)` — clean up handles
2. `RandomAccessState` entries removed (memory cleanup only)
3. **Do NOT roll back the transaction**
4. `SessionEntry.PauseStartedAt = now` — start pause timer
5. If `PausedTransactionTimeout` expires before reconnect → abort (rollback)

This is safe because the random-access path uses `lo_get`/`lo_put` (no server-side LO file descriptor leaks).

### Decision 6: gRPC disconnect does not cascade

**Status: ✅ Confirmed**

gRPC connection drops do NOT trigger transaction rollback. Rationale:
- `ServerCallContext.CancellationToken` is stream-level, not TCP-level
- Transient network flakiness should not cause data loss
- Transaction timeout (`CommittableTransaction`) is the correct safety net

### Decision 7: Timeout configuration model

**Status: ✅ Confirmed**

| Config key | Default | Purpose |
|-----------|:-------:|---------|
| `DefaultTransactionTimeout` | 30s | General transaction (gRPC) |
| `MaxTransactionTimeout` | 5min | General transaction cap |
| `BlobStreamTransactionTimeout` | 24h | BlobStream WS transaction default |
| `MaxBlobStreamTransactionTimeout` | 72h | BlobStream WS transaction cap |
| `PausedTransactionTimeout` | 5min | WS disconnect pause state timeout |

Consumer can specify `suggested_timeout` in `BeginSession`; server finalizes within bounds (existing negotiation logic).

---

## Part 3: Implementation plan

### Phase 1: Fix BlobStream random-access position state 🔴

**Files:** `PostgresBlobDriver.cs`

The existing `RandomAccessState` infrastructure is in place and actually **correct** — the position tracking via `state.Position` in `ReadAsync` and `WriteAsync` already works. The earlier analysis flagged this incorrectly; re-review shows:
- `ReadAsync` uses `lo_get(oid, state.Position, count)` and advances `state.Position`
- `WriteAsync` uses `lo_put(oid, state.Position, data)` and advances `state.Position`
- `SeekAsync` updates `state.Position`

However, the 4 skipped tests from the prior session need to be re-evaluated against the current implementation to confirm they pass.

**Action item:** Run the 4 previously skipped BlobStream random-access integration tests and verify pass/fail status.

### Phase 2: Remove OpCode.Commit

**Files:** 
- `OpCode.cs` — remove `Commit = 0x07` constant
- `BlobStreamMessage.cs` — keep `BuildAckResponse` (used by other ops)
- `BlobStreamConnectionHandler.cs` — remove `HandleCommitAsync`, remove `OpCode.Commit` from dispatch `switch`
- `BLOB-STREAM-DESIGN.md` — update OpCode table

### Phase 3: Session store transaction association

**Files:**
- `BlobStreamSessionStore.cs`
- `BlobStreamSessionEntry.cs`

Changes:
- Add `BeginTransactionResult? TransactionResult` to `BlobStreamSessionEntry`
- Add `string? ActiveConnectionId` to `BlobStreamSessionEntry`
- Add `DateTime? PauseStartedAt` to `BlobStreamSessionEntry`
- Modify `Create()` to optionally accept a `BeginTransactionResult`
- Add `TryReconnect(string sessionToken)` → returns `(bool reconnected, ...)`
- Modify `CleanupExpiredSessions` to also check `PauseStartedAt` against `PausedTransactionTimeout`

### Phase 4: ConnectionHandler lifecycle refactor

**Files:**
- `BlobStreamConnectionHandler.cs`
- `HandleManager.cs` (possibly, for connection registry)

Changes:
- On WS connect: check session for existing transaction → create or reuse
- On WS disconnect: clean handles only, keep transaction
- On WS reconnect: associate new connection ID with existing session/transaction
- Handshake response: add `reconnected` flag
- On transaction timeout: abort (close WS if open)
- On pause timeout: abort (rollback transaction)

### Phase 5: Protocol handshake extension

**Files:**
- `BlobStreamMessage.cs` — extend `BuildHandshakeResponse` to include `reconnected` flag
- `OpCode.cs` — optionally add `Heartbeat` for connection liveness detection (deferred)

Handshake response format (extended):
```
[4B txIdLen][UTF8 txId][8B expiresAtUnixMs][1B flags]
Flags: bit 0 = reconnected
```

### Phase 6: Configuration updates

**Files:**
- `CoordinatorOptions.cs` — add `PausedTransactionTimeout`
- `appsettings.json` — add BlobStream timeout config, remove `MaxRetryCount`/`RetryBackoffBase`
- `DETAILED-DESIGN.md` — already updated §6
- `BLOB-STREAM-DESIGN.md` — update OpCode table, add reconnection model, update flow examples

### Phase 7: Clean up other small defects

- **BlobDelete LO cleanup**: Add `lo_unlink` before `DELETE FROM adaptor_blob_store`
- **ShutdownAsync integration**: Register with `app.Lifetime.ApplicationStopping`
- **ConnectionRegistry** (optional): Track active WS connections for conflict detection

---

## Part 4: Research findings

### PostgreSQL Large Object behavior with `lo_get`/`lo_put`

- `lo_get(oid, offset, count)` — reads directly from OID, no file descriptor needed
- `lo_put(oid, offset, data)` — writes directly to OID, no file descriptor needed
- Both operations are transactional — they participate in the current `NpgsqlTransaction`
- The OID-based operations survive WS disconnects because the PG connection + transaction remain alive
- `Seek(End, ...)` and `TruncateAsync` use `lo_open`/`lo_close` temporarily, but these are self-contained within a single call

### gRPC reconnection characteristics

- `GrpcChannel` does NOT automatically reconnect TCP connections at the channel level
- `ServerCallContext.CancellationToken` is tied to HTTP/2 stream, NOT TCP connection
- Server-side handlers cannot detect client TCP disconnects without application-level heartbeat
- gRPC keepalive pings are at the HTTP handler level, not propagated to application code
- This confirms that transaction timeout is the correct mechanism for lifecycle management

### WebSocket reconnection patterns

- Session-token-based reconnection is a well-established pattern (used by AWS, SignalR, etc.)
- Server-side state (transaction, handles) needs to be indexed by token for reconnection
- Reconnection requires: server keeps state alive, client presents token, server associates new socket with old state
- Per-handle position must be tracked by consumer's client library; server does not persist it across reconnects
