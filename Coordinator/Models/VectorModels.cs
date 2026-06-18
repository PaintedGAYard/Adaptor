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
/// 向量 Upsert 请求。
/// DenseVector 和 SparseVector 可独立设置（储存时可同时传入两者以支持混合检索），
/// 但至少必须设置其中之一。检索时 VectorSearchRequest 只会使用其中一种。
/// </summary>
public sealed record VectorUpsertRequest(
    string Collection,
    float[]? DenseVector = null,
    SparseVector? SparseVector = null,
    string? Id = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    int? Dimension = null)
{
    public static VectorUpsertRequest FromDense(
        string collection, float[] dense, string? id = null,
        IReadOnlyDictionary<string, object?>? metadata = null, int? dimension = null)
        => new(collection, dense, null, id, metadata, dimension);

    public static VectorUpsertRequest FromSparse(
        string collection, SparseVector sparse, string? id = null,
        IReadOnlyDictionary<string, object?>? metadata = null, int? dimension = null)
        => new(collection, null, sparse, id, metadata, dimension);

    public static VectorUpsertRequest FromBoth(
        string collection, float[] dense, SparseVector sparse, string? id = null,
        IReadOnlyDictionary<string, object?>? metadata = null, int? dimension = null)
        => new(collection, dense, sparse, id, metadata, dimension);
}

/// <summary>向量 Upsert 结果</summary>
public sealed record VectorUpsertResult(
    string Id,
    bool Success,
    string? ErrorMessage = null);

/// <summary>向量搜索请求</summary>
public sealed record VectorSearchRequest(
    string Collection,
    float[]? DenseVector = null,
    SparseVector? SparseVector = null,
    int TopK = 10,
    IReadOnlyDictionary<string, object?>? Filter = null,
    int? Dimension = null)
{
    public static VectorSearchRequest FromDense(
        string collection, float[] dense, int topK = 10,
        IReadOnlyDictionary<string, object?>? filter = null, int? dimension = null)
        => new(collection, dense, null, topK, filter, dimension);

    public static VectorSearchRequest FromSparse(
        string collection, SparseVector sparse, int topK = 10,
        IReadOnlyDictionary<string, object?>? filter = null, int? dimension = null)
        => new(collection, null, sparse, topK, filter, dimension);
}

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
