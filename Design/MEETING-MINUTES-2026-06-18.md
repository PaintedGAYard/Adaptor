# Adaptor — 设计讨论会议纪要

> **日期**: 2026-06-18  
> **项目**: Adaptor — 基于 pg+pgvector 的 .NET 10 分布式事务中间件  
> **状态**: 最终稿

---

## 议程

1. 确定系统定位与部署形态
2. 确定 Wire Protocol 方案
3. 确定操作语义与事务语义的分离策略
4. 确定分布式事务协调策略
5. 确定一致性模型与错误处理
6. 确定连接管理与超时方案
7. 确定项目结构

---

## 讨论详情

### 1. 部署形态与系统定位

**问题**: 中间件是独立服务、嵌入式库还是 ASP.NET Core 中间件？

**决策**: **独立服务**。
- 只通过 gRPC wire protocol 与 consumer 通信
- 向下通过依赖注入（DI）加载不同的 Storage Driver
- Consumer 是缺乏分布式事务基础设施的运行时（Python、JavaScript、Go 等）

---

### 2. Wire Protocol

**问题**: 使用什么传输协议？service 如何拆分？

**决策**:
- 协议：**完全自定义的 gRPC 协议**
- Service 组织：**统一服务，不同 endpoint path**
  - 明确：gRPC 的 `service` 关键字在此项目中**不是自包含的 service 单元**
  - 仅视为 **endpoint group**（端点分组）
  - 真正的 service 单元是"围绕中间件事务对象生命周期管理形成的 API 整体"
- 即：一个 `service Adaptor { ... }` 包含事务、SQL、Vector、BLOB 所有 RPC

---

### 3. 操作语义与事务语义

**问题**: SQL/Vector/BLOB 的操作差异很大，如何抽象？

**决策**: **统一的事务语义，分离的操作语义**
- **操作语义分离**：SQL、Vector、BLOB 各自保留独立的操作 API，不强求统一
  - `SqlQuery` / `SqlExecute`
  - `VectorSearch` / `VectorUpsert`
  - `BlobUpload` / `BlobDownload`
- **事务语义统一**：三者的事务生命周期由中间件协调为统一的 `Begin` / `Commit` / `Rollback`
- 对外暴露**仅**简单事务语义（Begin/Commit/Rollback），全部内部实现细节对外不可见

---

### 4. 分布式事务协调策略

**问题**: 如何在 .NET 层面协调三个异构存储的事务？

**决策**:
- 核心价值定位：利用 .NET **高效的 DTC 基础设施**（`System.Transactions`），将复杂的分布式事务映射为简单事务，向缺乏 DTC 的运行时暴露
- **中间件职责**：将三个独立的事务生命周期映射为进程内同一个统一的 .NET `Transaction` 对象
  - 下游事务按需启动（延迟 Enlistment）
  - 与中间件事务同时结束（Commit / Rollback）
- **无需自定义 Transaction 子类**（除非 .NET 内置设施不满足需求时再考虑）
- Driver 通过 .NET 标准接口（`IEnlistmentNotification`、`IPromotableSinglePhaseNotification` 等）实现分布式事务支持
- **Driver 提供完整的分布式事务支持，中间件只负责聚合**
- 利用 .NET `CommittableTransaction` 作为顶层事务对象

**内部协调流程**:
1. `BeginTransaction` → 创建 `CommittableTransaction`
2. 数据操作 → 按需 `Enlist` Driver（延迟注册）
3. `Commit` → .NET 自动触发两阶段提交
4. `Rollback` → .NET 通知所有 Enlisted Driver 回滚

---

### 5. 一致性模型与错误处理

**问题**: 跨存储事务失败时如何处理？

**决策**: **All or Nothing**，降低初期实现难度，但保留升级空间。

**All or Nothing 策略**:
- **Phase 1 — Prepare**：所有 Driver 投票，任一失败则全部回滚
- **Phase 2 — Commit**：成功者等待失败者自动重试
  - 重试次数可配置（默认 3 次）
  - 使用指数退避（默认基数 100ms）
  - 重试耗尽仍然失败 → **全部回滚**（已成功的通过补偿回滚）
- **补偿要求**：Driver 需实现回滚/补偿逻辑（`IEnlistmentNotification.Rollback`）

---

### 6. 连接管理与超时

**问题**: 如何管理事务生命周期和连接？

**决策**:
- 使用 gRPC 内置的 HTTP/2 keepalive ping（无需应用层心跳协议）
- 中间件暴露 `SetTimeout` RPC，允许 Consumer 控制事务级别超时
- 服务端配置：
  - `DefaultTransactionTimeout`（默认 30s）
  - `SessionIdleTimeout`（默认 60s）
- 超时未 Commit 的事务自动 Rollback
- gRPC 连接断开时，关联的活跃事务自动 Rollback

---

### 7. 项目结构

**问题**: 代码如何组织？

**决策**:
- 当前阶段：**单项目**（单体 .csproj）
- 当需要自定义 Driver 接口时：将接口抽离为独立项目（`Adaptor.Core`）
- Driver 实现放在 `Drivers/` 子目录中，按存储类型分类（Sql / Vector / Blob）
- 每个 Driver 是自包含的类，通过 DI 注册到中间件

---

## 关键概念定义

| 概念 | 定义 |
|------|------|
| Transaction | .NET `System.Transactions.Transaction` 实例，代表一个工作单元 |
| Enlistment | Driver 将自身及关联本地事务注册到 .NET Transaction 的过程 |
| Session | Consumer 与中间件之间的 gRPC 会话上下文，绑定到一个活跃事务 |
| Driver | 实现对特定存储引擎的适配器，提供事务能力声明和数据操作 |
| Endpoint Group | gRPC `service` 关键字在此项目中仅作为方法分组，非自包含单元 |
| Service Unit | 围绕中间件事务对象生命周期管理形成的 API 整体 |

---

## 示例场景验证

### 例 1: 全 PostgreSQL 方案

| 存储 | 实现 | 事务协调 |
|------|------|---------|
| SQL | PgSQL (Npgsql) | 同库，可共享连接，PSPE 优化 |
| Vector | pgvector | 同库，与 SQL 共享 PG 事务 |
| BLOB | BYTEA + Large Object | 同库，与 SQL 共享 PG 事务 |

**分析**: 三者同库场景下，实际不需要分布式事务。`IPromotableSinglePhaseNotification` 使中间件在此场景零开销。

### 例 2: 混合方案

| 存储 | 实现 | 事务协调 |
|------|------|---------|
| SQL | MySQL | `IEnlistmentNotification` 2PC |
| Vector | SQLite-Vector | `IEnlistmentNotification` 2PC |
| BLOB | AWS S3 | 应用层补偿（预上传 → 确认/回滚删除） |

**分析**: 需完整 2PC。中间件的 All or Nothing 策略在此场景发挥核心价值。

---

## 待议事项（下一阶段）

以下事项未在本轮讨论中涉及，需在后续会议决定：

1. **认证与授权** — mTLS / Token？
2. **可观测性** — OpenTelemetry 集成策略
3. **配置管理** — 连接字符串、Driver 配置的管理方式
4. **持久化** — 事务状态的持久化与恢复
5. **测试策略** — 异构存储组合的集成测试方案
6. **gRPC API 细节** — 数据操作 RPC 的具体 message 结构（需求冻结后）
7. **部署拓扑** — 单副本还是多副本？多副本时事务状态如何同步？

---

## 参与者

- 设计讨论双方已就上述所有决策点达成一致。

---

*会议纪要结束*
