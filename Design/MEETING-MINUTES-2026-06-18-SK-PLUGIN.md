# Adaptor — 第三轮重构会议纪要：SK Plugin 集成

> **日期**: 2026-06-18  
> **主题**: SK Plugin 层设计与 Query Provider 选型  
> **状态**: 最终稿

---

## 议程

1. 确定 Adaptor 的 SK 集成方式：作为 SK 中间件（Plugin 层）而非 Driver 包装
2. 确定 Query Provider 选型方向

---

## 讨论详情

### 1. SK 集成方式

**问题**：SK 应该包装 Driver（将每个 Driver 的能力暴露为 Plugin）还是包装 Coordinator（将统一能力暴露为 Plugin）？

**背景**：
- 项目规划的其中一个暴露方向是作为 SK 中间件
- 隔壁组只需要 P0 工具，不需要 baby sit
- SK `KernelFunction` 是天然的统一查询接口

**决策**：**包装 Coordinator**，而非单独包装每个 Driver。

```
SK Consumer (kernel.InvokeAsync)
        │
        ▼
Adaptor Plugin Layer (薄转发层)
  ├─ TransactionPlugin    (BeginTransaction / Commit / Rollback)
  ├─ RelationalPlugin     (Execute / Query)
  └─ VectorSearchPlugin   (Search)
        │
        ▼
Coordinator + Driver 核心（不变）
```

理由：
- Plugin 层不包含业务逻辑——只是把 Coordinator 能力暴露为 SK KernelFunction
- gRPC 端点也可以转发到 Plugin，两者共享同一逻辑路径
- 未来非关系 Driver 加入时，Plugin 层保持不变，只需新增 Driver 实现

### 2. Query Provider 选型

**问题**：Should Adaptor implement a LINQ-like query provider (e.g. FilterExpression tree, IQueryable, etc.)?

**决策**：**当前不需要实现 LINQ Provider**。

| 方案 | 结论 |
|------|------|
| **LINQ `IQueryable<T>`** | 🔴 实现代价极高，且表达式树无法通过 gRPC 序列化 |
| **FilterExpression 树** | 🟡 可选但非紧急。当前 `SQL WHERE + RelationalParameter` 已覆盖 Relational 场景 |
| **SK Plugin + SQL WHERE 片段** | 🟢 当前方案。消费端写 `whereClause` + `parameters` |
| **（未来）FilterExpression 翻译器** | 等到需要非关系 Driver 时再引入。在 SK Plugin 内部做翻译，消费端无感 |

### 3. 新增文件

| 文件 | 说明 |
|------|------|
| `Coordinator/Plugins/TransactionPlugin.cs` | 事务生命周期（Begin/Commit/Rollback） |
| `Coordinator/Plugins/RelationalPlugin.cs` | 关系数据库通用操作（Execute/Query） |
| `Coordinator/Plugins/VectorSearchPlugin.cs` | 向量搜索（Search） |
| `ServiceCollectionExtensions.cs` | 更新，注册 Plugin 到 DI |

### 4. NuGet 依赖

`Microsoft.SemanticKernel.Abstractions` 1.77.0 — 仅包含 `[KernelFunction]` 属性和必要接口，不引入完整 SK 运行时。

---

## 实施检查清单

- [x] 添加 `Microsoft.SemanticKernel.Abstractions` NuGet 依赖
- [x] 创建 `TransactionPlugin.cs`
- [x] 创建 `RelationalPlugin.cs`
- [x] 创建 `VectorSearchPlugin.cs`
- [x] 更新 `ServiceCollectionExtensions.cs` 注册 Plugin
- [x] 更新 `DETAILED-DESIGN.md`（SK 集成章节 + 项目结构）
- [ ] 更新 `Program.cs` 示例（可选，Plugin 通过 DI 自动加载）

---

*会议纪要结束*
