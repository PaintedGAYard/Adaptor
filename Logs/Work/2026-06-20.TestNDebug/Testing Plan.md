# Testing Plan — 2026-06-20

## 目标

按照自底向上的顺序，对 Adaptor 各子系统进行业务逻辑测试与调试：
1. 以子系统为单元编写测试
2. 调试直至结果符合设计
3. 每个单元完成后中止汇报
4. 所有子系统完成后，进行跨子系统集成测试

## 子系统分层 & 权责边界

```
Service (gRPC endpoint group)     ← 最上层，无业务逻辑，纯协议转换
  └─ Coordinator (事务协调 + SK Plugin 转发)
       ├─ TransactionCoordinator — 事务生命周期管理
       ├─ SessionManager — 会话 & 超时管理
       ├─ Plugins — SK Plugin 薄转发层
       └─ Models / Abstractions — 数据模型 & 能力接口
            └─ Driver.Postgre (PostgreSqlDriver, PgVectorDriver, PostgresBlobDriver)  ← 最底层
```

| 子系统 | 权责 | 测试策略 |
|--------|------|---------|
| **Driver.Postgre** | 实际数据库操作、事务 enlistment、SQL 执行、向量搜索、BLOB 存取 | 单元测试(契约) + Testcontainers 集成测试(真实数据库行为) |
| **Coordinator** | 事务生命周期、Driver 编排、会话管理、Plugin 转发 | 单元测试(mock driver) + 补齐边界路径 |
| **Service** | gRPC 消息 ↔ 内部模型转换、错误映射 | 单元测试(工具方法) + 集成测试(gRPC 服务上下文) |
| **Integration** | 全链路：gRPC → Plugin → Coordinator → Driver | Testcontainers + mock gRPC context |

## 测试阶段 (Phase)

### Phase 1: Driver.Postgre

| # | 任务 | 状态 | 说明 |
|---|------|------|------|
| 1.1 | PgVectorDriver 集成测试 | ✅ Done | CREATE EXTENSION IF NOT EXISTS vector 修复验证 |
| 1.2 | PostgreSqlDriver 集成测试 | 📝 WIP | SQL execute/query/parameter binding 真实数据库测试 |
| 1.3 | PostgresBlobDriver 修复 + 集成测试 | 🔧 Debug | Large Object API 函数名错误 (lo_write → lowrite 等) |
| 1.4 | 统一 Testcontainers 镜像为 pg17 | ⏳ Pending | 匹配部署环境 `pgvector/pgvector:0.8.0-pg17` |

### Phase 2: Coordinator

| # | 任务 | 状态 | 说明 |
|---|------|------|------|
| 2.1 | TransactionCoordinator 补齐 | ⏳ Pending | commit 失败路径、rollback 异常、shutdown 边界、cancellation |
| 2.2 | SessionManager 补齐 | ⏳ Pending | cleanup、dispose 线程安全、超时清理路径 |
| 2.3 | Plugin 层补齐 | ⏳ Pending | error 传播、参数传递、cancellation |
| 2.4 | Models 边界情况 | ⏳ Pending | 缺失的 null/empty 边界测试 |

### Phase 3: Service

| # | 任务 | 状态 | 说明 |
|---|------|------|------|
| 3.1 | AdaptorServiceContext 工具方法 | ⏳ Pending | ConvertStatus、ObjectToValue、ProtoValueToObject |
| 3.2 | gRPC Service 实现 | ⏳ Pending | Transaction/Relational/Vector/BLOB service |

### Phase 4: 中间件集成

| # | 任务 | 状态 | 说明 |
|---|------|------|------|
| 4.1 | Coordinator + Plugins 集成 | ⏳ Pending | 多 driver 2PC 提交/回滚 |
| 4.2 | Service + Coordinator 集成 | ⏳ Pending | gRPC 请求 → 内部调用 → 响应映射 |
| 4.3 | 全链路端到端 | ⏳ Pending | 完整管道：Testcontainers PG → ... → Driver |

## 已知问题 (已发现)

| # | 问题 | 子系统 | 状态 |
|---|------|--------|------|
| B1 | `CREATE EXTENSION IF NOT EXISTS vector` 未执行 | PgVectorDriver | ✅ 已修复 |
| B2 | PostgresBlobDriver 使用错误的 LO API (`lo_write(oid,0,data)` 应为 `lo_open` + `lowrite`) | PostgresBlobDriver | 🔧 修复中 |
| B3 | Testcontainers 镜像使用 pg16，部署环境为 pg17 | 测试基础设施 | ⚠️ 需要统一 |

## 部署环境参考

docker-compose.yml 来自 `deployment/docker/Adaptor/`:

- PostgreSQL 镜像: `pgvector/pgvector:0.8.0-pg17`
- 连接地址: `Host=adaptor-driver-postgres;Port=5432;Database=adaptor`
- 测试应使用相同镜像版本以保证兼容性
