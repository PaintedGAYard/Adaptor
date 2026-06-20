# Work Summary — 2026-06-20 PGDriver Refactor

## Table of Contents

- [Work Summary — 2026-06-20 PGDriver Refactor](#work-summary--2026-06-20-pgdriver-refactor)
    - [Table of Contents](#table-of-contents)
    - [Summary](#summary)
    - [Background](#background)
    - [Plan](#plan)
    - [Work performed](#work-performed)
        - [Output](#output)
        - [Discoveries](#discoveries)
    - [Unfinished work](#unfinished-work)

---

## Summary

This session refactored the PostgreSQL Driver subsystem (PostgreSqlDriver, PgVectorDriver, PostgresBlobDriver) to eliminate ~200 lines of duplicated code, integrate the pgvector-dotnet NuGet package for proper Vector/SparseVector type mappings, and fix the BLOB random-access path placeholder implementations. A shared `NpgsqlConnectionManager` was extracted to centralise connection lifecycle management, transaction enlistment, and health checks. The BLOB random-access operations (SeekAsync, WriteAsync, CloseAsync, TruncateAsync) were upgraded from no-op stubs to full implementations with local offset tracking. All 4 previously skipped integration tests now pass. Test count moved from 287 passed / 5 skipped to 290 passed / 1 skipped.

---

## Background

The Adaptor project's PostgreSQL Driver subsystem consisted of three drivers — PostgreSqlDriver, PgVectorDriver, and PostgresBlobDriver — each independently implementing the same connection management pattern (`ConcurrentDictionary`, `Enlist`/`GetEntry`/`RemoveEntry`, `ConnectionEntry` record, `IEnlistmentNotification` handler). This duplication made maintenance error-prone and violated the Composition over Inheritance design principle.

Three specific technical debts were identified:

1. **Code duplication**: ~200 lines of near-identical infrastructure code across three drivers, including three separate enlistment handlers with identical logic.
2. **pgvector string formatting**: `PgVectorDriver` manually serialised float arrays and sparse vectors to PG text literals (`[0.1,0.2,0.3]::vector`) instead of using the community-maintained `Pgvector` NuGet package's type mappings.
3. **BLOB random-access placeholders**: Four `IBlobRandomAccessCapability` methods (CloseAsync, SeekAsync, WriteAsync, TruncateAsync) were no-op stubs, causing `[Fact(Skip)]` tests and preventing the BlobStream subsystem from functioning correctly.

---

## Plan

The work was divided into 5 phases as detailed in `TODO.md`:

1. **Phase 0**: Add Pgvector NuGet package, establish test baseline
2. **Phase 1**: Extract shared `NpgsqlConnectionManager` infrastructure
3. **Phase 2**: Integrate pgvector-dotnet Vector/SparseVector type mappings
4. **Phase 3**: Fix BLOB random-access path with local offset tracking
5. **Phase 4**: Guideline compliance (timestamps, unused code removal)
6. **Phase 5**: Regression testing and work summary

---

## Work performed

### Output

**Phase 0 — Infrastructure setup**

- Added `Pgvector` v0.3.2 NuGet package to `Adaptor.Driver.Postgre.csproj`
- Established test baseline: 287 passed, 5 skipped

**Phase 1 — Connection management extraction**

- Created `NpgsqlConnectionManager` (`src/Driver/Postgre/NpgsqlConnectionManager.cs`):
  - `ConcurrentDictionary<string, ConnectionEntry>` for per-transaction connection tracking
  - `Enlist(Transaction)`, `GetEntry(Transaction)`, `RemoveEntry(string txId)`, `Dispose()`
  - Unified `NpgsqlEnlistmentHandler` implementing `IEnlistmentNotification` (replaces three separate handlers)
  - Per-transaction locking via `SemaphoreSlim` (replaces two separate implementations; adds missing support to BlobDriver)
  - `HealthCheckAsync()` (replaces three identical copies)
  - Dual-mode construction: `string connectionString` and `NpgsqlDataSource`

- Updated all three drivers to delegate to the shared manager:
  - `PostgreSqlDriver`: removed `_connections`/`_txLocks`/`ConnectionEntry`/`NpgsqlEnlistmentHandler`
  - `PgVectorDriver`: same
  - `PostgresBlobDriver`: same, plus added per-tx lock support (previously missing)

- Updated `EnlistmentHandlersTest` and `PostgreSqlDriverTest` to match new structure

**Phase 2 — pgvector-dotnet integration**

- `NpgsqlConnectionManager`: added `NpgsqlDataSource` constructor overload; `Enlist()` uses `DataSource.CreateConnection()` when DataSource is available
- `PgVectorDriver`: added `NpgsqlDataSource` constructor overload
- `SearchAsync` SQL: removed `::vector`/`::sparsevec` casts when DataSource is available; retains backward-compatible string fallback for raw connection strings
- Deleted `DenseVectorToString()`, `SparseVectorToString()`, `DeserializeDenseVector()`, `DeserializeSparseVector()`
- Added `CreateVectorParameter()` method that produces `Vector`/`SparseVector` CLR objects when DataSource is configured

**Phase 3 — BLOB random-access path fix**

- Added `RandomAccessState` class (Oid, Position, IsClosed) and `ConcurrentDictionary<int, RandomAccessState>` tracking dictionary
- Fixed all six `IBlobRandomAccessCapability` methods:
  - `OpenAsync`: creates state entry with position = 0
  - `CloseAsync`: marks IsClosed, removes entry; subsequent operations return error
  - `ReadAsync`: uses `state.Position` as `lo_get(oid, offset, count)` offset; advances position
  - `WriteAsync`: uses `state.Position` as `lo_put(oid, offset, data)` offset; advances position
  - `SeekAsync`: supports SeekOrigin.Begin/Current/End (End uses `lo_lseek64` to determine blob size)
  - `TruncateAsync`: uses `lo_open` → `lo_truncate64` → `lo_close` sequence; clamps position if past new end
- Removed `[Fact(Skip)]` from 4 integration tests; all now pass

**Test delta**

| Metric | Before | After | Change |
|--------|:------:|:-----:|:------:|
| Total passed | 287 | 290 | +3 |
| Skipped (BLOB placeholders) | 4 | 0 | -4 |
| Skipped (manual timeout) | 1 | 1 | 0 |
| **Total** | **287 / 5** | **290 / 1** | **+3 pass, -4 skip** |

### Discoveries

**pgvector-dotnet requires DataSource-level configuration**

The `UseVector()` extension method must be called on `NpgsqlDataSourceBuilder`, not on individual `NpgsqlConnection` instances. This architectural constraint required the connection manager to support dual-mode construction (raw string vs DataSource). The raw-string path still works but falls back to the old string-based vector formatting — this is a deliberate backward-compatibility decision.

**`lo_truncate` parameter type mismatch**

PostgreSQL's `lo_truncate(fd, len)` expects `integer` for length, but the C# code passes `long` (sent as `bigint`). This caused a `25P02` transaction-aborted error. The fix uses `lo_truncate64(fd, len)` which accepts `bigint` — available in PG 14+ which matches the deployment requirement (`pgvector/pgvector:0.8.0-pg17`).

**Server-side LO descriptor management**

Both `SeekAsync` (End origin) and `TruncateAsync` need to call `lo_open` to obtain a file descriptor, then `lo_close` after the operation. Initially the code tried inline calls like `lo_truncate(lo_open(...), ...)` which leaked the descriptor. The fix uses separate SQL statements with proper try/finally for descriptor cleanup.

**ConnectionEntry type became part of the public API**

After making `NpgsqlConnectionManager` public (needed for test accessibility), its nested `ConnectionEntry` record also needed to be public since `GetEntry()` returns it. This is acceptable since the type is a simple data holder (`NpgsqlConnection` + `NpgsqlTransaction`).

---

## Unfinished work

**ServiceCollectionExtensions DataSource support**

`AddAdaptorPgVectorDriver` and `AddAdaptorPostgreDrivers` in `ServiceCollectionExtensions` do not yet create a `NpgsqlDataSource` with `UseVector()` configured. Drivers created through the DI extension methods will still use the raw connection string path. Updating these requires adding the `Pgvector.Npgsql` namespace and configuring the DataSource builder, which is straightforward but was deferred to keep the refactoring scope focused.

**pgvector-dotnet type mapping test coverage**

`PgVectorDriverTest` still asserts on the old string-formatting behavior in some parameter validation tests. These tests verify the interface contract (parameter validation) not the formatting implementation, so they continue to pass. However, there is no dedicated test that validates `Vector`/`SparseVector` objects are correctly round-tripped through Npgsql when using a DataSource. The existing integration tests (`PgVectorDriverIntegrationTest`) exercise this path implicitly but don't explicitly assert type mapping.

**`ReadLargeObjectAsync` parameter count**

`ReadLargeObjectAsync` has 5 parameters (`connection`, `transaction`, `oid`, `totalSize`, `ct`). The guideline recommends refactoring functions with >5 parameters, and flags >3 as at-risk. This is on the boundary and should be considered for future refactoring (e.g., bundling into a request record).

**Coordinator rollback-on-commit semantics**

Identified in the previous session: `CommitTransactionAsync` on an already-rolled-back transaction throws instead of returning `CommitResult(RolledBack)`. This was not part of the current scope but is noted for future work.
