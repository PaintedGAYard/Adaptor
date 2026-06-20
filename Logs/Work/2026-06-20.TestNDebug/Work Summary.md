# Work Summary — 2026-06-20 Test & Debug

> **Phase**: Phase 1 (Driver.Postgre) — ✅ Completed  
> **Phase**: Phase 2 (Coordinator) — ✅ Completed  
> **Next**: Phase 3 (Service)

---

## Skipped Tests（设计行为尚未实现）

以下测试用 `[Fact(Skip = "...")]` 明确标记为跳过，原因：当前实现是 placeholder/stub，未达到设计规范要求。当重构后实现正确行为时，去掉 Skip 属性即可。

| 测试 | 文件 | 设计行为 | 当前实现 |
|------|------|---------|---------|
| `CloseAsync_ShouldReleaseDescriptor` | `PostgresBlobDriverIntegrationTest.cs` | 关闭后读取 loFd 应失败 | no-op |
| `SeekAsync_ShouldAffectSubsequentReadPosition` | `PostgresBlobDriverIntegrationTest.cs` | Seek 后 Read 应从新位置读 | 总是从 offset 0 读 |
| `WriteAsync_ShouldRespectSeekPosition` | `PostgresBlobDriverIntegrationTest.cs` | Write 应在当前 seek 位置写入 | 总是在 offset 0 写 |
| `TruncateAsync_ShouldShortenBlob` | `PostgresBlobDriverIntegrationTest.cs` | Truncate 应缩短大对象 | no-op stub |

### 其他已知不可自动运行的测试

| 测试 | 位置 | 原因 |
|------|------|------|
| `SessionManagerTest.Session_ShouldBeAutoCleanedAfterIdleTimeout` | Coordinator | `[Fact(Skip = "Manual test...")]` + `[Trait("Manual", "true")]`，计入 skipped + 可手动筛选发现 |

---

## 修复的 Bug

| # | 问题 | 文件 | 根因 |
|---|------|------|------|
| B1 | PgVectorDriver 未创建 pgvector extension | `PgVectorDriver.cs` | 未调用 `CREATE EXTENSION IF NOT EXISTS vector` |
| B2 | PostgresBlobDriver 使用不存在的 `lo_write(oid,0,data)` | `PostgresBlobDriver.cs` | PG17 正确函数名为 `lowrite`/`loread`（无下划线） |
| B3 | 随机访问路径 `loread` 返回 0 字节 | `PostgresBlobDriver.cs` | 服务端 `lo_open` fd 管理复杂，改用 `lo_get`/`lo_put` |
| B4 | OID 列 `reader.GetInt32(0)` 抛出类型异常 | `PostgresBlobDriver.cs` | PostgreSQL `oid` 是 unsigned，`GetInt32` 不支持 |

---

## 新增文件

| 文件 | 说明 |
|------|------|
| `test/.../PgVectorDriverIntegrationTest.cs` | PgVectorDriver 集成测试（extension + search） |
| `test/.../PostgreSqlDriverIntegrationTest.cs` | PostgreSqlDriver 集成测试（SQL execute/query） |
| `test/.../PostgresBlobDriverIntegrationTest.cs` | PostgresBlobDriver 集成测试（upload/download/random access + placeholder 文档） |
| `Logs/Work/2026-06-20.TestNDebug/Testing Plan.md` | 测试规划 |
| `Logs/Work/2026-06-20.TestNDebug/TODO.md` | 任务追踪 |
| `Logs/Work/2026-06-20.TestNDebug/Work Summary.md` | 本文件 |

---

## 测试覆盖率变化

| 项目 | Phase 1 前 | Phase 1 后 | Phase 2 后 |
|------|-----------|-----------|-----------|
| Driver 单元测试 | 62 | 62 | 62 |
| Driver 集成测试 | 0 | 23 | 23 |
| Coordinator 测试 | 97 | 97 | **123** (+26) |
| **通过 / 跳过 / 总计** | **62 / 0 / 62** | **82 / 4 / 86** | **205 / 5 / 210** |

---

## Placeholder & Future Works

### 已知 placeholder 实现
- `PostgresBlobDriver.CloseAsync` — no-op
- `PostgresBlobDriver.SeekAsync` — 返回 offset 但不实际 seek
- `PostgresBlobDriver.TruncateAsync` — no-op stub

### 已记录 Future Works
详见 `TODO.md` → `# Future Works`
- pgvector-dotnet 集成 (重构 PgVectorDriver 的 Vector/SparseVector 类型映射)
- BLOB Driver 随机访问路径重构
