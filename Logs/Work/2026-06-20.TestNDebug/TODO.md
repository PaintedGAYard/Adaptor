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

## Phase 2: Coordinator (current)

- [ ] **2.1** TransactionCoordinator 补齐
  - [ ] Commit 失败路径 (CommitCoreAsync 异常)
  - [ ] Rollback 异常路径
  - [ ] Shutdown 超时等待
  - [ ] Cancellation 测试
  - [ ] Dispose 幂等性
- [ ] **2.2** SessionManager 补齐
  - [ ] 已关闭 session 的 Touch 行为
  - [ ] Dispose 线程安全
  - [ ] CleanupExpiredSessions 路径
- [ ] **2.3** Plugin 层补齐
  - [ ] TransactionPlugin 错误传播
  - [ ] RelationalPlugin 参数传递
  - [ ] VectorSearchPlugin 过滤条件
  - [ ] Cancellation 测试
- [ ] **2.4** Models 边界情况

---

## Phase 3: Service

- [ ] **3.1** AdaptorServiceContext 工具方法
- [ ] **3.2** gRPC Service 实现

---

## Phase 4: 中间件集成

- [ ] **4.1** Coordinator + Plugins 集成
- [ ] **4.2** Service + Coordinator 集成
- [ ] **4.3** 全链路端到端

---

## Future Works

### pgvector-dotnet 集成 (优先级: 高)
- 当前 `PgVectorDriver` 使用原始 SQL 传递 vector/sparsevec 参数 (字符串格式化)
- `pgvector-dotnet` (NuGet: `Pgvector`) 提供了 `Vector` / `HalfVector` / `SparseVector` 类型和 Npgsql 类型映射
- 通过 `dataSourceBuilder.UseVector()` 启用后可以直接传参: `cmd.Parameters.AddWithValue(new Vector(embedding))`
- 不再需要手动 `DenseVectorToString` / `SparseVectorToString` 格式化
- SQL 中可直接使用 `@vector::vector` 或 `@vector::sparsevec`，无需字符串转换

### BLOB Driver 重构 (优先级: 中)
- 当前随机访问路径 (`OpenAsync`/`ReadAsync`/`WriteAsync` 等) 已改为使用 `lo_get`/`lo_put`
- `SeekAsync` 和 `CloseAsync` 现在是 no-op (因为 `lo_get`/`lo_put` 不维护位置状态)
- `TruncateAsync` 是 stub
- pgvector-dotnet 重构时应清理这些实现

### 大对象 SQL 函数参考
来自 PostgreSQL 17 Manual (§33.4 Server-Side Functions):
- `lo_from_bytea(loid oid, data bytea)` → oid — 创建并写入 LO
- `lo_put(loid oid, offset bigint, data bytea)` → void — 在偏移处写入
- `lo_get(loid oid [, offset bigint, length integer])` → bytea — 读取内容
- `loread(fd integer, len integer)` → bytea — 从描述符读取
- `lowrite(fd integer, data bytea)` → integer — 写入描述符
