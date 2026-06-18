namespace Adaptor.Coordinator.Models;

/// <summary>
/// Sparse vector representation for vector search.
/// </summary>
/// <remarks>
/// <paramref name="Indices"/> and <paramref name="Values"/> must have the same length;
/// indices must be strictly increasing.
/// </remarks>
/// <param name="Indices">Dimension indices (must be strictly increasing).</param>
/// <param name="Values">Values at each index; length must match <paramref name="Indices"/>.</param>
public sealed record SparseVector(
    int[] Indices,
    float[] Values);

/// <summary>
/// Vector similarity search against a relational database.
/// Provide either <paramref name="DenseVector"/> or <paramref name="SparseVector"/>, but not both.
/// </summary>
/// <remarks>
/// Filtering uses a native SQL WHERE clause with <c>@param</c> references
/// (referenced in <paramref name="Parameters"/>) to avoid SQL injection.
/// </remarks>
/// <param name="Table">Table or view name. Schema-qualified if needed.</param>
/// <param name="VectorColumn">Column storing the vector embedding.</param>
/// <param name="DenseVector">Dense query vector as a float array.</param>
/// <param name="SparseVector">Sparse query vector; mutually exclusive with <paramref name="DenseVector"/>.</param>
/// <param name="TopK">Maximum number of results to return.</param>
/// <param name="WhereClause">Optional SQL WHERE clause using <c>@param</c> placeholders.</param>
/// <param name="Parameters">Parameters referenced in <paramref name="WhereClause"/>.</param>
public sealed record RelationalVectorSearchRequest(
    string Table,
    string VectorColumn,
    float[]? DenseVector = null,
    SparseVector? SparseVector = null,
    int TopK = 10,
    string? WhereClause = null,
    IReadOnlyList<RelationalParameter>? Parameters = null);

/// <param name="Id">Row identifier (primary key value).</param>
/// <param name="Score">Similarity score; higher is more similar (range 0–1).</param>
/// <param name="Metadata">Additional columns stored with the vector; null if none.</param>
public sealed record VectorSearchHit(
    string Id,
    float Score,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <param name="Hits">Results ordered by descending score; empty list if none.</param>
/// <param name="Duration">Search execution time.</param>
/// <param name="ErrorMessage">Null on success; describes the failure otherwise.</param>
public sealed record VectorSearchResult(
    IReadOnlyList<VectorSearchHit> Hits,
    TimeSpan Duration,
    string? ErrorMessage = null);
