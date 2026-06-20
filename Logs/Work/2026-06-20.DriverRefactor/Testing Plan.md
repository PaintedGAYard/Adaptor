# Testing Plan — 2026-06-20 PGDriver Refactor

## 目标

对 PGDriver 子系统进行重构，以测试驱动确保行为不变或符合设计目标。

## 分层策略

```
┌─────────────────────────────────────────────────────┐
│  Phase 5: 回归测试 (dotnet test 全套)                  │
├─────────────────────────────────────────────────────┤
│  Phase 3-4: BLOB + Guideline 测试                     │
│  └─ 移除 4 个 Skip，验证新实现                           │
├─────────────────────────────────────────────────────┤
│  Phase 2: pgvector-dotnet 集成测试                     │
│  └─ 更新向量格式化相关断言                               │
│  └─ 新增 Vector/SparseVector 类型映射测试               │
├─────────────────────────────────────────────────────┤
│  Phase 1: 连接管理重构测试                              │
│  └─ 现有测试全部通过 = 行为不变证明                      │
├─────────────────────────────────────────────────────┤
│  Phase 0: 测试基线                                     │
│  └─ 运行 dotnet test，记录 236 pass / 7 skip          │
└─────────────────────────────────────────────────────┘
```

## 测试类型

| 类型 | 范围 | 方式 |
|------|------|------|
| **单元测试** | 每个 Driver 的契约测试 | xUnit + NSubstitute mock |
| **集成测试** | Driver + 真实 PG (Testcontainers) | Testcontainers.PostgreSql |
| **回归测试** | 每个 Phase 完成后 | `dotnet test` |

## 每个 Phase 的测试验证

### Phase 0 & 1
- 运行所有现有测试 → 全部 pass（行为不变）
- 重构后不改变任何业务逻辑

### Phase 2
- 更新 `PgVectorDriverTest` 中关于向量参数的测试
  - `SearchAsync_ShouldAcceptDenseVector` → 内部实现变了但行为不变
  - `SearchAsync_ShouldAcceptSparseVector` → 同上
- 更新 `PgVectorDriverIntegrationTest`
  - 确保 pgvector-dotnet 映射正确工作
- 新增：验证 `Vector` 和 `SparseVector` 对象作为 Npgsql 参数正确传递

### Phase 3
- 移除 4 个 `[Fact(Skip)]` 标记
  - `CloseAsync_ShouldReleaseDescriptor`
  - `SeekAsync_ShouldAffectSubsequentReadPosition`
  - `WriteAsync_ShouldRespectSeekPosition`
  - `TruncateAsync_ShouldShortenBlob`
- 确认它们在真实 PG 上通过
- 新增边界情况测试（Seek beyond end, Read after close 等）

### Phase 4
- 无需测试变更
- 构建检查确保无警告

### Phase 5
- 最终 `dotnet test` 全套
- 预期: 240 pass / 3 skip

## 测试运行命令

```powershell
# 全部测试
dotnet test

# 仅 Driver 测试
dotnet test test/Adaptor.Test.Driver

# 仅 Coordinator 测试
dotnet test test/Adaptor.Test.Coordinator

# 仅 Service 测试
dotnet test test/Adaptor.Test.Service

# 跳过集成测试（不需要 Docker）
dotnet test --filter "FullyQualifiedName!~IntegrationTest"
```
