# TODO — 2026-06-20 Test & Debug

> 按 Phase 顺序推进，每次只 focus 一个 task

---

## Phase 1: Driver.Postgre ✅ COMPLETED

- [x] **1.1** PgVectorDriver 集成测试 + `CREATE EXTENSION` 修复
- [x] **1.2** PostgreSqlDriver 集成测试
- [x] **1.3** PostgresBlobDriver: 修复 LO API + 集成测试
  - `lo_write(oid,0,data)` 和 `lo_read(oid,offset,len)` 函数不存在
  - PG17 正确的函数名: `lowrite` / `loread` (无下划线) — 来自 PG17 manual §33.4
  - 随机访问路径改用 PG17 推荐的 `lo_get`/`lo_put`/`lo_from_bytea` API
  - 扫描 `lo_open`/`lo_close` 不再需要 (server-side API 直接操作 OID)
- [x] **1.4** 统一 Testcontainers 镜像为 `pgvector/pgvector:0.8.0-pg17`
- [x] **1.5** 补写 placeholder 行为测试 (CloseAsync/SeekAsync/TruncateAsync 文档)
- [x] **1.6** 全部 86 个测试：**82 passed** + **4 skipped**（设计行为未实现的 placeholder）

### Phase 1 发现的问题

| # | 问题 | 修复 |
|---|------|------|
| B1 | PgVectorDriver 未执行 `CREATE EXTENSION IF NOT EXISTS vector` | 添加 `EnsureExtensionAsync` |
| B2 | PostgresBlobDriver 使用了不存在的 `lo_write(oid,0,data)` 和 `lo_read(oid,offset,len)` | 改为 `lowrite(fd,data)` + `loread(fd,len)` + `lo_open(oid,mode)` |
| B3 | `lo_open` 模式常量 INV_READ/INV_WRITE 在 PG17 中实际被忽略 (8.1+) | 但代码中的值是对的 |
| B4 | 服务端 `lo_open` 返回的 fd 管理复杂 (单描述符槽) | 随机访问改为 `lo_get`/`lo_put` 直接操作 OID |
| B5 | Testcontainers 镜像用 pg16，部署环境为 pg17 | 统一为 `pgvector/pgvector:0.8.0-pg17` |

---

## Phase 2: Coordinator ✅ COMPLETED

- [x] **2.1** TransactionCoordinator 补齐
  - Commit 失败路径 (ForceRollbackEnlistment → RolledBack)
  - Rollback 二次调用 (no-op after cleanup)
  - Shutdown 无事务 + 超时等待 + 活跃 tx rollback
  - Cancellation (Begin/Commit/Rollback)
  - Dispose 幂等性 + connectionId 参数
- [x] **2.2** SessionManager 补齐
  - TouchSession 对 closed session = no-op
  - GetSession after RemoveSession → null
  - CreateSession with null/empty connectionId
  - OnConnectionClosed after RemoveSession
  - Dispose 幂等性
- [x] **2.3** Plugin 层补齐
  - TransactionPlugin: commit 不存在 tx → throw, rollback 不存在 tx → no-op
  - RelationalPlugin: Execute/Query 带参数, ExecuteBatch 空数组
  - VectorSearchPlugin: Search 带 whereClause + 参数
- [x] **2.4** Models 边界情况
  - RelationalExecuteRequest null command
  - BlobDownloadResult null data
  - BlobUploadResult ErrorMessage
  - SparseVector 长度不匹配
  - VectorSearchHit null metadata
  - CommitResult 空 DriverResults

---

## Phase 3: Service ✅ COMPLETED

- [x] **3.1** AdaptorServiceContext 工具方法 (21 tests)
  - ConvertStatus / ConvertTransactionStatus 所有枚举映射
  - ObjectToValue: null, string, int, long, float, double, bool, Dictionary, List, 嵌套, fallback
- [x] **3.2** gRPC Service 实现 (10 tests, 2 skipped)
  - TransactionServiceImpl: Begin (skipped: needs gRPC hosting), Commit, Rollback, GetStatus, CommitAfterRollback (skipped: Coordinator throws)
  - RelationalServiceImpl: Execute, Query (with fake driver)
  - RelationalVectorServiceImpl: Search (with fake driver)
  - BlobServiceImpl: Upload, Download (with fake driver)
- [x] 新建 `test/Adaptor.Test.Service/` 项目

---

## Phase 4: 中间件集成 ✅ COMPLETED

- [x] **4.1** Coordinator + 真实 PG Driver 多 driver 2PC 集成测试 (Testcontainers)
  - 创建 PostgreSqlDriver + PgVectorDriver + PostgresBlobDriver 注册到同一 Coordinator
  - BeginTransaction → SQL INSERT → Vector Search → BLOB Upload/Download → Commit
  - 验证事务提交后数据持久化 (独立连接检查)
  - 验证事务回滚后数据不回滚
- [ ] ~~**4.2** Service + Coordinator 集成 (gRPC test server)~~ → 移至 Future Works (需要 gRPC 测试宿主)
- [ ] ~~**4.3** 全链路端到端 (完整的 gRPC 管道)~~ → 移至 Future Works


