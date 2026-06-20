namespace Adaptor.Coordinator.Models;

/// <summary>
/// Sparse vector representation for vector search.
/// </summary>
/// <remarks>
/// <paramref name="Indices"/> and <paramref name="Values"/> must have the same length;
/// indices must be strictly increasing.
/// </remarks>
/// <param name="indices">Dimension indices (must be strictly increasing).</param>
/// <param name="values">Values at each index; length must match <paramref name="indices"/>.</param>
/// <remarks>
/// Uses <c>double</c> for components so the Coordinator remains DB-agnostic.
/// Drivers cast to the DB-specific precision (<c>float</c> for pgvector, etc.) as needed.
/// </remarks>
public sealed record SparseVector
{
    /// <summary>Dimension indices (strictly increasing).</summary>
    public int[] Indices { get; }

    /// <summary>Values at each index; length matches <see cref="Indices"/>.</summary>
    public double[] Values { get; }

    public SparseVector(int[] indices, double[] values)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(values);

        if (indices.Length != values.Length)
            throw new ArgumentException(
                $"Indices length ({indices.Length}) must match Values length ({values.Length}).",
                nameof(indices));

        // Validate strictly increasing indices
        for (var i = 1; i < indices.Length; i++)
        {
            if (indices[i] <= indices[i - 1])
                throw new ArgumentException(
                    $"Indices must be strictly increasing; index[{i}]={indices[i]} <= index[{i - 1}]={indices[i - 1]}.",
                    nameof(indices));
        }

        Indices = indices;
        Values = values;
    }

    public void Deconstruct(out int[] Indices, out double[] Values)
    {
        Indices = this.Indices;
        Values = this.Values;
    }
}

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
/// <param name="DenseVector">Dense query vector as a double array (DB-agnostic; drivers cast as needed).</param>
/// <param name="SparseVector">Sparse query vector; mutually exclusive with <paramref name="DenseVector"/>.</param>
/// <param name="TopK">Maximum number of results to return.</param>
/// <param name="WhereClause">Optional SQL WHERE clause using <c>@param</c> placeholders.</param>
/// <param name="Parameters">Parameters referenced in <paramref name="WhereClause"/>.</param>
public sealed record RelationalVectorSearchRequest(
    string Table,
    string VectorColumn,
    double[]? DenseVector = null,
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
