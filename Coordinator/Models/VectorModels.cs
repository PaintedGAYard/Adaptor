namespace Adaptor.Coordinator.Models;

/// <summary>向量 Upsert 请求</summary>
public sealed record VectorUpsertRequest(
    string Collection,
    float[] Vector,
    string? Id = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>向量 Upsert 结果</summary>
public sealed record VectorUpsertResult(
    string Id,
    bool Success,
    string? ErrorMessage = null);

/// <summary>向量搜索请求</summary>
public sealed record VectorSearchRequest(
    string Collection,
    float[] Vector,
    int TopK = 10,
    IReadOnlyDictionary<string, object?>? Filter = null);

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
