# Work Summary — 2026-06-20 Middleware Functional Analysis

## Table of Contents

- [Work Summary — 2026-06-20 Middleware Functional Analysis](#work-summary--2026-06-20-middleware-functional-analysis)
    - [Table of Contents](#table-of-contents)
    - [Summary](#summary)
    - [Background](#background)
    - [Work performed](#work-performed)
        - [Output](#output)
        - [Discoveries](#discoveries)
    - [Resolved design issues](#resolved-design-issues)
    - [Remaining items](#remaining-items)

---

## Summary

This was a design analysis session. The goal was to identify what's still missing before the Adaptor Middleware becomes serviceable, by separating core business logic from advanced business logic and evaluating current implementation coverage.

Key findings:
1. The middleware is **~75% serviceable** — core transaction lifecycle, relational, vector, and BLOB operations are implemented and tested (324 tests, 0 skipped).
2. **BlobStream data-plane random access** is the biggest blocker — Seek/Read/Write position state is broken in `PostgresBlobDriver`, making the entire WebSocket data plane unreliable.
3. A **design document contradiction** was identified and fixed: §6.1 described a manual per-driver 2PC orchestration loop that contradicts §4.2's correct statement ".NET 内部触发两阶段提交". The implementation correctly delegates to .NET `System.Transactions`.
4. Dead configuration (`MaxRetryCount`, `RetryBackoffBase`) was removed from `CoordinatorOptions` with clarifying comments.

---

## Background

The Adaptor project's middleware layer has been under active development. After three prior sessions (Design → Driver Refactor → Driver Completion → Test & Debug), the codebase reached 324 passing tests with 0 skips.

This session took a step back to evaluate: **what is actually missing before this middleware can be called "serviceable"?**

---

## Work performed

### Output

1. **Middleware-Functional-Analysis.md** (`Logs/Work/2026-06-20.FunctionalAnalysis/`)
   - Separated all middleware features into Core (必须实现) vs Advanced (可延迟)
   - Identified 7 defects/gaps with severity ratings
   - Evaluated test coverage gaps
   - Overall readiness: 🟡 75%

2. **Design document fix — DETAILED-DESIGN.md §6**
   - Rewrote §6.1 to correctly describe delegation to .NET `System.Transactions`
   - Removed `MaxRetryCount` and `RetryBackoffBase` from the config table
   - Rewrote §6.3 (rollback) to match .NET TM model instead of manual iteration
   - Updated §4.1 bullet 3/5 to reflect Coordinator's actual role

3. **CoordinatorOptions.cs cleanup**
   - Removed `MaxRetryCount` and `RetryBackoffBase` properties
   - Added XML doc explaining why they were removed

---

## Resolved design issues

**The "2PC retry" misunderstanding**

Initial analysis flagged "CommitTransaction 缺少真正的 2PC 重试 + Partial 状态" as a defect. Review revealed this was not a defect — the implementation correctly delegates to .NET `System.Transactions.CommittableTransaction`, which handles 2PC internally via `IEnlistmentNotification`. The confusion originated from §6.1 of the design document, which described a manual per-driver 2PC loop that was never implemented and shouldn't be implemented against .NET's TM.

After discussion with the user, §6.1 was rewritten to accurately describe the delegation model:
- Coordinator calls `committableTransaction.Commit()`
- .NET TM calls `IEnlistmentNotification.Prepare()` on each enlisted driver
- All Prepared → .NET TM calls `IEnlistmentNotification.Commit()` on each driver
- Any ForceRollback → .NET TM calls `IEnlistmentNotification.Rollback()` on all drivers

---

## Remaining items

### Must-fix before serviceable

| Priority | Issue | Area |
|:--------:|-------|------|
| 🔴 | BlobStream random-access position state broken | `PostgresBlobDriver` |
| 🟡 | gRPC connection disconnect not wired to SessionManager.OnConnectionClosed | `Program.cs` / gRPC lifecycle |
| 🟡 | ShutdownAsync not registered with ApplicationStopping | `Program.cs` |

### Should-fix

| Priority | Issue | Area |
|:--------:|-------|------|
| 🟡 | BlobDelete doesn't call lo_unlink (LO storage leak) | `BlobServiceImpl` |
| 🟢 | appsettings.json missing BlobStream timeout config | Configuration |
| 🟢 | BlobStream OpCode.Commit not in design doc | Design alignment |

### Design discussions deferred

- BlobStream OpCode.Commit: WebSocket path can commit transactions, but this isn't documented in BLOB-STREAM-DESIGN.md (contradicts Control/Data Plane separation principle)
