namespace Adaptor.Test.Coordinator;

/// <summary>
/// Tests for the Coordinator model records and enums.
/// Design-based: derived from DETAILED-DESIGN.md §3.4, §4.4, REFACTOR §3, REFACTOR-2 §3.
/// Verifies serialization, parameter validation, and structural correctness.
/// </summary>
public sealed class ModelsTest
{
    // ──────────────────────────────────────────────
    // CommitStatus — 设计文档 REFACTOR §3
    // ──────────────────────────────────────────────

    [Fact]
    public void CommitStatus_ShouldDefineAllStatuses()
    {
        Assert.Equal(1, (int)CommitStatus.Committed);
        Assert.Equal(2, (int)CommitStatus.Partial);
        Assert.Equal(3, (int)CommitStatus.RolledBack);
        Assert.Equal(4, (int)CommitStatus.Timeout);
    }

    // ──────────────────────────────────────────────
    // RelationalParameter — 设计文档 3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void RelationalParameter_ShouldStoreNameAndValue()
    {
        var param = new RelationalParameter("@id", 42);
        Assert.Equal("@id", param.Name);
        Assert.Equal(42, param.Value);
    }

    [Fact]
    public void RelationalParameter_ShouldAcceptNullValue()
    {
        var param = new RelationalParameter("@name", null);
        Assert.Null(param.Value);
    }

    // ──────────────────────────────────────────────
    // RelationalExecuteRequest / Result — 设计文档 3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void RelationalExecuteRequest_ShouldStoreCommandAndParameters()
    {
        var request = new RelationalExecuteRequest(
            "INSERT INTO t VALUES (@v)",
            [new RelationalParameter("@v", 1)]);

        Assert.Equal("INSERT INTO t VALUES (@v)", request.Command);
        Assert.Single(request.Parameters!);
    }

    [Fact]
    public void RelationalExecuteRequest_ShouldAllowNullParameters()
    {
        var request = new RelationalExecuteRequest("SELECT 1");
        Assert.Null(request.Parameters);
    }

    [Fact]
    public void RelationalExecuteResult_ShouldStoreAffectedRows()
    {
        var result = new RelationalExecuteResult(5, TimeSpan.FromMilliseconds(10));
        Assert.Equal(5, result.AffectedRows);
        Assert.Equal(TimeSpan.FromMilliseconds(10), result.Duration);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void RelationalExecuteResult_ShouldAcceptErrorMessage()
    {
        var result = new RelationalExecuteResult(0, TimeSpan.Zero, "error");
        Assert.Equal("error", result.ErrorMessage);
    }

    // ──────────────────────────────────────────────
    // RelationalQueryRequest / Result — 设计文档 3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void RelationalQueryResult_ShouldStoreRows()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
        };

        var result = new RelationalQueryResult(rows, TimeSpan.FromMilliseconds(5));

        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["id"]);
        Assert.Equal("Alice", result.Rows[0]["name"]);
    }

    [Fact]
    public void RelationalQueryResult_ShouldAllowEmptyRows()
    {
        var result = new RelationalQueryResult([], TimeSpan.Zero);
        Assert.Empty(result.Rows);
    }

    // ──────────────────────────────────────────────
    // RelationalVectorSearchRequest — 设计文档 REFACTOR-2 §3
    // ──────────────────────────────────────────────

    [Fact]
    public void RelationalVectorSearchRequest_ShouldRequireTableAndVectorColumn()
    {
        var request = new RelationalVectorSearchRequest("items", "embedding", [0.1f, 0.2f]);
        Assert.Equal("items", request.Table);
        Assert.Equal("embedding", request.VectorColumn);
    }

    [Fact]
    public void RelationalVectorSearchRequest_ShouldAcceptDenseVector()
    {
        var request = new RelationalVectorSearchRequest("t", "v", [1f, 2f, 3f]);
        Assert.NotNull(request.DenseVector);
        Assert.Null(request.SparseVector);
    }

    [Fact]
    public void RelationalVectorSearchRequest_ShouldAcceptSparseVector()
    {
        var sparse = new SparseVector([0, 5], [0.5f, 0.8f]);
        var request = new RelationalVectorSearchRequest("t", "v", null, sparse, TopK: 5);
        Assert.NotNull(request.SparseVector);
        Assert.Null(request.DenseVector);
        Assert.Equal(5, request.TopK);
    }

    [Fact]
    public void RelationalVectorSearchRequest_ShouldAcceptWhereClauseAndParameters()
    {
        var request = new RelationalVectorSearchRequest(
            "t", "v", [1f], TopK: 10,
            WhereClause: "category = @cat",
            Parameters: [new RelationalParameter("@cat", "electronics")]);

        Assert.Equal("category = @cat", request.WhereClause);
        Assert.Single(request.Parameters!);
    }

    [Fact]
    public void RelationalVectorSearchRequest_ShouldHaveDefaultTopK()
    {
        var request = new RelationalVectorSearchRequest("t", "v", [1f]);
        Assert.Equal(10, request.TopK);
    }

    // ──────────────────────────────────────────────
    // VectorSearchHit / Result — 设计文档 3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void VectorSearchHit_ShouldStoreIdAndScore()
    {
        var hit = new VectorSearchHit("abc-123", 0.95f);
        Assert.Equal("abc-123", hit.Id);
        Assert.Equal(0.95f, hit.Score);
    }

    [Fact]
    public void VectorSearchHit_ShouldAcceptMetadata()
    {
        var metadata = new Dictionary<string, object?> { ["title"] = "Doc 1" };
        var hit = new VectorSearchHit("abc", 0.9f, metadata);
        Assert.NotNull(hit.Metadata);
        Assert.Equal("Doc 1", hit.Metadata["title"]);
    }

    [Fact]
    public void VectorSearchResult_ShouldStoreHits()
    {
        var hits = new List<VectorSearchHit>
        {
            new("a", 0.9f),
            new("b", 0.8f),
        };

        var result = new VectorSearchResult(hits, TimeSpan.FromMilliseconds(15));

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(15), result.Duration);
    }

    // ──────────────────────────────────────────────
    // SparseVector — 设计文档 3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void SparseVector_ShouldStoreIndicesAndValues()
    {
        var sv = new SparseVector([0, 1, 5], [0.1f, 0.3f, 0.8f]);
        Assert.Equal([0, 1, 5], sv.Indices);
        Assert.Equal([0.1f, 0.3f, 0.8f], sv.Values);
    }

    // ──────────────────────────────────────────────
    // Blob models — 设计文档 3.4, BLOB-STREAM §5.2
    // ──────────────────────────────────────────────

    [Fact]
    public void BlobUploadRequest_ShouldStoreKeyAndData()
    {
        var request = new BlobUploadRequest("photo.jpg", [1, 2, 3], "image/jpeg");
        Assert.Equal("photo.jpg", request.Key);
        Assert.Equal([1, 2, 3], request.Data);
        Assert.Equal("image/jpeg", request.ContentType);
    }

    [Fact]
    public void BlobUploadResult_ShouldStoreSize()
    {
        var result = new BlobUploadResult("photo.jpg", 1024);
        Assert.Equal(1024, result.Size);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void BlobDownloadResult_ShouldStoreData()
    {
        var result = new BlobDownloadResult("photo.jpg", [1, 2, 3], "image/jpeg");
        Assert.Equal([1, 2, 3], result.Data);
    }

    [Fact]
    public void BlobOpenResult_ShouldStoreLoFdAndBlobSize()
    {
        var result = new BlobOpenResult(5, 1000);
        Assert.Equal(5, result.LoFd);
        Assert.Equal(1000, result.BlobSize);
    }

    [Fact]
    public void BlobReadResult_ShouldStoreDataAndBytesRead()
    {
        var result = new BlobReadResult([10, 20, 30], 3);
        Assert.Equal(3, result.BytesRead);
    }

    // ──────────────────────────────────────────────
    // CommitResult / DriverCommitResult — 设计文档 3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void CommitResult_ShouldStoreStatusAndDriverResults()
    {
        var dr = new DriverCommitResult("pg", true, 0);
        var result = new CommitResult(CommitStatus.Committed, [dr]);

        Assert.Equal(CommitStatus.Committed, result.Status);
        Assert.Single(result.DriverResults);
    }

    [Fact]
    public void DriverCommitResult_ShouldStoreDriverNameAndRetryCount()
    {
        var dr = new DriverCommitResult("pgvector", true, 2);
        Assert.Equal("pgvector", dr.DriverName);
        Assert.True(dr.Success);
        Assert.Equal(2, dr.RetryCount);
    }

    // ──────────────────────────────────────────────
    // BeginTransactionResult — 设计文档 REFACTOR §2
    // ──────────────────────────────────────────────

    [Fact]
    public void BeginTransactionResult_ShouldStoreTransactionAndExpiry()
    {
        using var tx = new CommittableTransaction();
        var expiresAt = DateTime.UtcNow.AddMinutes(5);
        var result = new BeginTransactionResult(tx, expiresAt);

        Assert.Same(tx, result.Transaction);
        Assert.Equal(expiresAt, result.ExpiresAt);
    }

    // ──────────────────────────────────────────────
    // SessionContext — 设计文档 REFACTOR §2
    // ──────────────────────────────────────────────

    [Fact]
    public void SessionContext_ShouldUpdateLastActivityOnTouch()
    {
        var session = new SessionContext();
        var before = session.LastActivityAt;
        Thread.Sleep(5);
        session.Touch();
        Assert.True(session.LastActivityAt > before);
    }
}
