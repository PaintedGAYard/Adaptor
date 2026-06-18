namespace Adaptor.Coordinator.Models;

/// <summary>
/// 稀疏向量表示。
/// Indices 和 Values 长度必须相同，且 Indices 必须递增。
/// pgvector 格式：{idx1:val1,idx2:val2,...}
/// </summary>
public sealed record SparseVector(
    int[] Indices,
    float[] Values);

/// <summary>
/// 关系数据库向量搜索请求。
/// 使用原生 SQL WHERE 片段（通过 @param 引用 Parameters）来支持过滤，
/// 复用关系数据库参数化契约，无 SQL 注入风险。
/// DenseVector 和 SparseVector 二选一。
/// </summary>
public sealed record RelationalVectorSearchRequest(
    string Table,
    string VectorColumn,
    float[]? DenseVector = null,
    SparseVector? SparseVector = null,
    int TopK = 10,
    string? WhereClause = null,
    IReadOnlyList<RelationalParameter>? Parameters = null);

/// <summary>搜索结果命中项</summary>
public sealed record VectorSearchHit(
    string Id,
    float Score,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>向量搜索结果</summary>
public sealed record VectorSearchResult(
    IReadOnlyList<VectorSearchHit> Hits,
    TimeSpan Duration,
    string? ErrorMessage = null);
