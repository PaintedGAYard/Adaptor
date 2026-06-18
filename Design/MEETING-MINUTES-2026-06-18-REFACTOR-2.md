# Adaptor — 第二轮重构会议纪要

> **日期**: 2026-06-18  
> **主题**: 命名体系重构、向量子系统职责边界确定、过滤能力调研  
> **状态**: 最终稿

---

## 议程

1. 确定命名体系（SQL → Relational，Vector → RelationalVector）
2. 确定向量子系统职责边界（仅 Search，CRUD 走 SQL）
3. 调研跨类别向量数据库过滤能力并确定 Adaptor 策略
4. 讨论 collection 概念的去留
5. 讨论 Blob streaming 的影响

---

## 讨论详情

### 1. 命名体系重构

**问题**：当前 `Sql` 和 `Vector` 前缀暗示这些是独立的子系统，但实际上它们都基于关系数据库扩展。

**决策**：

| 旧名 | 新名 | 理由 |
|------|------|------|
| `ISqlExecuteCapability` | `IRelationalExecuteCapability` | 关系数据库通用执行能力 |
| `ISqlQueryCapability` | `IRelationalQueryCapability` | 关系数据库通用查询能力 |
| `IVectorSearchCapability` | `IRelationalVectorSearchCapability` | 关系数据库向量扩展搜索 |
| `DBSQL` (gRPC) | `DBRelational` | 对应新命名 |
| `DBVector` (gRPC) | `DBRelationalVector` | 仅 Search 端点 |
| `SqlServiceImpl` | `RelationalServiceImpl` | — |
| `VectorServiceImpl` | `RelationalVectorServiceImpl` | — |

命名统一为 `Relational` 前缀，明确反映"基于关系数据库扩展"的语义。未来加入非关系向量库时使用独立命名（如 `PineconeVector`）。

### 2. 向量子系统职责边界

**问题**：`IVectorCRUDCapability` 承担了 Upsert/Read/Delete/Batch 等精确语义操作，但与关系数据库的 SQL 能力重叠。

**决策**：
- **删除 `IVectorCRUDCapability`**，CRUD 操作由消费端通过 `DBRelational` 的原生 SQL 完成
- `IRelationalVectorSearchCapability` **仅保留 Search**（向量距离模糊匹配）
- 精确匹配（ID 查找）走 SQL `SELECT`
- 批量 Upsert 走 SQL `INSERT ... ON CONFLICT DO UPDATE`
- 批量 Delete 走 SQL `DELETE WHERE id = ANY(@ids)`

**理由**：
- 向量存储是关系数据库的一张特殊索引，不是独立存储层
- collection 概念只是 metadata 字段，不需要独立的能力接口
- 消费端已经在写 SQL，不需要额外抽象层

### 3. RelationalVectorSearchRequest 重新设计

**问题**：原 `VectorSearchRequest` 带有 `Collection` 字段和 `Filter` 字典，语义模糊且无法覆盖 SQL WHERE 的全部能力。

**决策**：
- 删除 `Collection` 字段 → 改为 `Table` + `VectorColumn`
- 删除 `Filter` 字典 → 改为原生 SQL `WhereClause` + `RelationalParameter[]`
- 消费端直接在 WHERE 片段中使用 `@param` 引用参数，复用关系数据库参数化契约

```csharp
public sealed record RelationalVectorSearchRequest(
    string Table,
    string VectorColumn,
    float[]? DenseVector,
    SparseVector? SparseVector,
    int TopK,
    string? WhereClause,
    IReadOnlyList<RelationalParameter>? Parameters);
```

### 4. 跨类别向量数据库过滤能力调研

**详细报告已单独产出**，核心结论：

| 类别 | 组内 filter 一致性 | Adaptor 策略 |
|------|:-----------------:|--------------|
| Relational-based | 🟢 高（都是 SQL） | `RelationalVectorSearchRequest.WhereClause` 直接接受 SQL WHERE |
| KV/Document-based | 🔴 低 | 仅保证 Layer 1（`$eq`），不承诺完整覆盖 |
| Pure Vector DB | 🔴 极低 | 各 Driver 独立翻译；不支持算子抛明确异常 |

三层过滤抽象方案（SimpleFilter → FilterExpression → NativeFilter）暂不实施，等有非关系 Driver 需求时再议。

### 5. Collection 概念

**问题**：collection 在各分类系统中语义不同，Adaptor 是否需要抽象？

**结论**：
- 对于 Relational 类，collection 只是 `WHERE collection = @c`，没有独立数据库对象
- 消费端通过 `Table` 参数自行管理数据隔离
- 暂不删除 `adaptor_vector_store` 表中的 `collection` 列（兼容现有数据），但文档标注为"消费端自行管理的 metadata 字段"
- 后续数据库无关层面需要 collection 抽象时再引入

### 6. Blob Streaming

**确认**：当前 `/Design/MEETING-MINUTES-2026-06-18-REFACTOR.md` 中的分析结论不变——现有实现不阻碍未来 streaming 能力。Streaming 作为独立 gRPC endpoint 添加，不影响现有 unary API。

---

## 实施检查清单（本轮）

- [x] `SqlModels.cs` → `RelationalModels.cs`（文件重命名 + 类型重命名）
- [x] `ISqlExecuteCapability.cs` → `IRelationalExecuteCapability.cs`
- [x] `ISqlQueryCapability.cs` → `IRelationalQueryCapability.cs`
- [x] `IVectorSearchCapability.cs` → `IRelationalVectorSearchCapability.cs`
- [x] **删除** `IVectorCRUDCapability.cs`
- [x] 更新 `VectorModels.cs`：删除所有 CRUD 类型，保留 SparseVector + SearchResult
- [x] 重新设计 `RelationalVectorSearchRequest`（Table/VectorColumn/WhereClause/Parameters）
- [x] 更新 `adaptor.proto`：`DBSQL`→`DBRelational`，`DBVector`→`DBRelationalVector`；删除 CRUD RPC
- [x] 更新 `AdaptorService.cs`：`SqlServiceImpl`→`RelationalServiceImpl`，`VectorServiceImpl`→`RelationalVectorServiceImpl`
- [x] 更新 `PgVectorDriver`：移除 CRUD，Search 改用新模型
- [x] 更新 `PostgreSqlDriver`：接口名更新
- [x] 更新 `Program.cs`：服务注册名更新
- [ ] 更新 `DETAILED-DESIGN.md`（本文档外）
- [ ] 更新 gRPC 协议文档

---

## 待议事项

1. **Collection 的去留** — 当前表结构保留 `collection` 列，后续是否彻底删除有待讨论
2. **非关系向量库 Driver 接口设计** — 需要具体需求时再设计
3. **通用 FilterExpression 树** — 暂不实施，等有跨 Driver filter 需求时再议

---

*会议纪要结束*
