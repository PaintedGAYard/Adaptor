using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Models;
using Adaptor.Coordinator.Services;

namespace Adaptor.Coordinator.Plugins;

/// <summary>
/// SK Plugin wrapping <see cref="IRelationalVectorSearchCapability"/> for vector search.
/// </summary>
/// <remarks>CRUD operations are handled via <see cref="RelationalPlugin"/> using native SQL.</remarks>
public sealed class VectorSearchPlugin
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ILogger<VectorSearchPlugin> _logger;

    public VectorSearchPlugin(TransactionCoordinator coordinator, ILogger<VectorSearchPlugin> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    [KernelFunction("vector_search")]
    [Description("Search for similar vectors using cosine distance")]
    public async Task<VectorSearchResult> SearchAsync(
        [Description("Active transaction ID")] string transactionId,
        [Description("Table name containing vectors")] string table,
        [Description("Vector column name")] string vectorColumn,
        [Description("Dense vector values (double[])")] double[]? denseVector = null,
        [Description("Sparse vector for keyword search")] SparseVector? sparseVector = null,
        [Description("Number of top results")] int topK = 10,
        [Description("Optional SQL WHERE clause with @params")] string? whereClause = null,
        [Description("Optional WHERE clause parameters")] RelationalParameter[]? parameters = null,
        CancellationToken ct = default)
    {
        var request = new RelationalVectorSearchRequest(
            Table: table,
            VectorColumn: vectorColumn,
            DenseVector: denseVector,
            SparseVector: sparseVector,
            TopK: topK,
            WhereClause: whereClause,
            Parameters: parameters?.AsReadOnly());

        return await _coordinator.ExecuteOnCapabilityAsync<IRelationalVectorSearchCapability, VectorSearchResult>(
            transactionId,
            (driver, tx) => driver.SearchAsync(request, tx, ct),
            ct);
    }
}
