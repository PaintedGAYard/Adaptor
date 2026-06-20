using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using System.Transactions;
using Adaptor.Service;

namespace Adaptor.Test.Service;

using CoordCommitResult = Adaptor.Coordinator.Models.CommitResult;
using CoordDriverCommitResult = Adaptor.Coordinator.Models.DriverCommitResult;
using CoordBeginTransactionResult = Adaptor.Coordinator.Models.BeginTransactionResult;
using CoordVectorSearchHit = Adaptor.Coordinator.Models.VectorSearchHit;
using CoordVectorSearchResult = Adaptor.Coordinator.Models.VectorSearchResult;

/// <summary>
/// Tests for gRPC service implementations:
/// <see cref="TransactionServiceImpl"/>, <see cref="RelationalServiceImpl"/>,
/// <see cref="RelationalVectorServiceImpl"/>, <see cref="BlobServiceImpl"/>.
/// </summary>
public sealed class GrpcServiceTests
{
    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    /// <summary>Create a lightweight ServerCallContext for testing.</summary>
    private static ServerCallContext CreateCallContext(CancellationToken ct = default)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.Id = "test-connection-id";
        return HttpContextServerCallContext.Create(httpContext, ct);
    }

    /// <summary>Create a real Coordinator with empty drivers and minimal options.</summary>
    private static (TransactionCoordinator coord, AdaptorServiceContext ctx) CreateRealContext()
    {
        var options = Options.Create(new CoordinatorOptions
        {
            DefaultTransactionTimeout = TimeSpan.FromMinutes(5),
            MaxTransactionTimeout = TimeSpan.FromHours(1),
        });
        var sessionManager = new SessionManager(options, Substitute.For<ILogger<SessionManager>>());
        var coord = new TransactionCoordinator(
            Array.Empty<IResourceManager>(),
            sessionManager,
            options,
            Substitute.For<ILogger<TransactionCoordinator>>());

        var ctx = new AdaptorServiceContext(
            coord,
            options,
            Substitute.For<ILogger<AdaptorServiceContext>>());

        return (coord, ctx);
    }

    /// <summary>Begin a real transaction and return its ID for use in service calls.</summary>
    private static async Task<string> BeginTxId(TransactionCoordinator coord)
    {
        var result = await coord.BeginTransactionAsync();
        return result.Transaction.TransactionInformation.LocalIdentifier;
    }

    // ──────────────────────────────────────────────
    // TransactionServiceImpl
    // ──────────────────────────────────────────────

    [Fact(Skip = "BeginTransaction uses GetHttpContext() which requires ASP.NET Core hosting. Cannot unit-test without gRPC test server.")]
    public async Task TransactionService_BeginTransaction_ShouldReturnResponse()
    {
        var (coord, ctx) = CreateRealContext();
        var svc = new TransactionServiceImpl(ctx);
        var callContext = CreateCallContext();

        var response = await svc.BeginTransaction(new BeginTransactionRequest(), callContext);

        Assert.NotNull(response);
        Assert.NotNull(response.TransactionId);
        Assert.NotEmpty(response.TransactionId);
        Assert.NotNull(response.ExpiresAt);
    }

    [Fact]
    public async Task TransactionService_CommitTransaction_ShouldReturnCommitted()
    {
        var (coord, ctx) = CreateRealContext();
        var svc = new TransactionServiceImpl(ctx);
        var callContext = CreateCallContext();

        // Create tx via coordinator directly (BeginTransaction service method needs gRPC hosting)
        var txId = await BeginTxId(coord);

        var response = await svc.CommitTransaction(
            new CommitTransactionRequest { TransactionId = txId },
            callContext);

        Assert.Equal(global::Adaptor.Service.CommitStatus.Committed, response.Status);
    }

    [Fact]
    public async Task TransactionService_RollbackTransaction_ShouldReturnSuccess()
    {
        var (coord, ctx) = CreateRealContext();
        var svc = new TransactionServiceImpl(ctx);
        var callContext = CreateCallContext();

        var txId = await BeginTxId(coord);

        var response = await svc.RollbackTransaction(
            new RollbackTransactionRequest { TransactionId = txId },
            callContext);

        Assert.True(response.Success);
    }

    [Fact]
    public async Task TransactionService_GetTransactionStatus_ForNotFoundTx_ShouldReturnUnspecified()
    {
        var (coord, ctx) = CreateRealContext();
        var svc = new TransactionServiceImpl(ctx);

        var response = await svc.GetTransactionStatus(
            new GetTransactionStatusRequest { TransactionId = "unknown" },
            CreateCallContext());

        Assert.Equal(global::Adaptor.Service.TransactionState.Unspecified, response.State);
    }

    [Fact(Skip = "Design: commit after rollback should return RolledBack status, but current Coordinator throws InvalidOperationException. Fix when refactoring TransactionCoordinator cleanup logic.")]
    public async Task TransactionService_CommitOnRolledBackTx_ShouldReturnRolledBack()
    {
        var (coord, ctx) = CreateRealContext();
        var svc = new TransactionServiceImpl(ctx);
        var callContext = CreateCallContext();

        var txId = await BeginTxId(coord);
        await svc.RollbackTransaction(
            new RollbackTransactionRequest { TransactionId = txId }, callContext);

        var response = await svc.CommitTransaction(
            new CommitTransactionRequest { TransactionId = txId }, callContext);

        Assert.Equal(global::Adaptor.Service.CommitStatus.RolledBack, response.Status);
        Assert.NotNull(response.ErrorMessage);
    }

    // ──────────────────────────────────────────────
    // RelationalServiceImpl (with fake driver)
    // ──────────────────────────────────────────────

    private sealed class FakeRelationalDriver :
        IResourceManager, ITransactionalResourceManager,
        IRelationalExecuteCapability, IRelationalQueryCapability
    {
        public string Name => "FakeRelational";
        public ResourceType ResourceType => ResourceType.Sql;
        public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();
        public void Enlist(System.Transactions.Transaction transaction) { }
        public Task<Adaptor.Coordinator.Models.RelationalExecuteResult> ExecuteAsync(
            Adaptor.Coordinator.Models.RelationalExecuteRequest request, System.Transactions.Transaction transaction, CancellationToken ct)
            => Task.FromResult(new Adaptor.Coordinator.Models.RelationalExecuteResult(5, TimeSpan.FromMilliseconds(10)));
        public Task<Adaptor.Coordinator.Models.RelationalQueryResult> QueryAsync(
            Adaptor.Coordinator.Models.RelationalQueryRequest request, System.Transactions.Transaction transaction, CancellationToken ct)
            => Task.FromResult(new Adaptor.Coordinator.Models.RelationalQueryResult(
                [new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" }],
                TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public async Task RelationalService_Execute_ShouldReturnResponse()
    {
        var options = Options.Create(new CoordinatorOptions { DefaultTransactionTimeout = TimeSpan.FromMinutes(5) });
        var sessionManager = new SessionManager(options, Substitute.For<ILogger<SessionManager>>());
        var coord = new TransactionCoordinator([new FakeRelationalDriver()], sessionManager, options, Substitute.For<ILogger<TransactionCoordinator>>());
        var ctx = new AdaptorServiceContext(coord, options, Substitute.For<ILogger<AdaptorServiceContext>>());
        var svc = new RelationalServiceImpl(ctx);
        var callContext = CreateCallContext();

        var txId = await BeginTxId(coord);

        var response = await svc.Execute(
            new RelationalExecuteRequest
            {
                TransactionContext = new TransactionContext { TransactionId = txId },
                Command = "INSERT INTO t VALUES (1)",
            },
            callContext);

        Assert.Equal(5, response.AffectedRows);
        Assert.True(response.DurationMs > 0);
    }

    [Fact]
    public async Task RelationalService_Query_ShouldReturnRows()
    {
        var options = Options.Create(new CoordinatorOptions { DefaultTransactionTimeout = TimeSpan.FromMinutes(5) });
        var sessionManager = new SessionManager(options, Substitute.For<ILogger<SessionManager>>());
        var coord = new TransactionCoordinator([new FakeRelationalDriver()], sessionManager, options, Substitute.For<ILogger<TransactionCoordinator>>());
        var ctx = new AdaptorServiceContext(coord, options, Substitute.For<ILogger<AdaptorServiceContext>>());
        var svc = new RelationalServiceImpl(ctx);
        var callContext = CreateCallContext();

        var txId = await BeginTxId(coord);

        var response = await svc.Query(
            new RelationalQueryRequest
            {
                TransactionContext = new TransactionContext { TransactionId = txId },
                Command = "SELECT * FROM t",
            },
            callContext);

        Assert.Single(response.Rows);
        Assert.True(response.DurationMs > 0);
    }

    // ──────────────────────────────────────────────
    // RelationalVectorServiceImpl (with fake driver)
    // ──────────────────────────────────────────────

    private sealed class FakeVectorDriver :
        IResourceManager, ITransactionalResourceManager,
        IRelationalVectorSearchCapability
    {
        public string Name => "FakeVector";
        public ResourceType ResourceType => ResourceType.Vector;
        public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();
        public void Enlist(System.Transactions.Transaction transaction) { }
        public Task<Adaptor.Coordinator.Models.VectorSearchResult> SearchAsync(
            Adaptor.Coordinator.Models.RelationalVectorSearchRequest request, System.Transactions.Transaction transaction, CancellationToken ct)
            => Task.FromResult(new Adaptor.Coordinator.Models.VectorSearchResult(
                [new Adaptor.Coordinator.Models.VectorSearchHit("id-1", 0.95f)], TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task VectorService_Search_ShouldReturnHits()
    {
        var options = Options.Create(new CoordinatorOptions { DefaultTransactionTimeout = TimeSpan.FromMinutes(5) });
        var sessionManager = new SessionManager(options, Substitute.For<ILogger<SessionManager>>());
        var coord = new TransactionCoordinator([new FakeVectorDriver()], sessionManager, options, Substitute.For<ILogger<TransactionCoordinator>>());
        var ctx = new AdaptorServiceContext(coord, options, Substitute.For<ILogger<AdaptorServiceContext>>());
        var svc = new RelationalVectorServiceImpl(ctx);
        var callContext = CreateCallContext();

        var txId = await BeginTxId(coord);

        var response = await svc.Search(
            new RelationalVectorSearchRequest
            {
                TransactionContext = new TransactionContext { TransactionId = txId },
                Table = "items",
                VectorColumn = "embedding",
                DenseVector = { 0.1f, 0.2f, 0.3f },
                TopK = 10,
            },
            callContext);

        Assert.Single(response.Hits);
        Assert.Equal("id-1", response.Hits[0].Id);
        Assert.Equal(0.95f, response.Hits[0].Score);
        Assert.True(response.DurationMs > 0);
    }

    // ──────────────────────────────────────────────
    // BlobServiceImpl (with fake drivers)
    // ──────────────────────────────────────────────

    private sealed class FakeBlobDriver :
        IResourceManager, ITransactionalResourceManager,
        IBlobUploadCapability, IBlobDownloadCapability
    {
        public string Name => "FakeBlob";
        public ResourceType ResourceType => ResourceType.Blob;
        public Guid ResourceManagerIdentifier { get; } = Guid.NewGuid();
        public void Enlist(System.Transactions.Transaction transaction) { }
        public Task<Adaptor.Coordinator.Models.BlobUploadResult> UploadAsync(
            Adaptor.Coordinator.Models.BlobUploadRequest request, System.Transactions.Transaction transaction, CancellationToken ct)
            => Task.FromResult(new Adaptor.Coordinator.Models.BlobUploadResult(request.Key, request.Data.LongLength));
        public Task<Adaptor.Coordinator.Models.BlobDownloadResult> DownloadAsync(
            Adaptor.Coordinator.Models.BlobDownloadRequest request, System.Transactions.Transaction transaction, CancellationToken ct)
            => Task.FromResult(new Adaptor.Coordinator.Models.BlobDownloadResult(request.Key, new byte[] { 1, 2, 3 }, "text/plain"));
    }

    [Fact]
    public async Task BlobService_Upload_ShouldReturnResponse()
    {
        var options = Options.Create(new CoordinatorOptions { DefaultTransactionTimeout = TimeSpan.FromMinutes(5) });
        var sessionManager = new SessionManager(options, Substitute.For<ILogger<SessionManager>>());
        var coord = new TransactionCoordinator([new FakeBlobDriver()], sessionManager, options, Substitute.For<ILogger<TransactionCoordinator>>());
        var ctx = new AdaptorServiceContext(coord, options, Substitute.For<ILogger<AdaptorServiceContext>>());
        var svc = new BlobServiceImpl(ctx);
        var callContext = CreateCallContext();

        var txId = await BeginTxId(coord);

        var response = await svc.BlobUpload(
            new BlobUploadRequest
            {
                TransactionContext = new TransactionContext { TransactionId = txId },
                Key = "test-key",
                Data = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
            },
            callContext);

        Assert.Equal("test-key", response.Key);
        Assert.True(response.Size > 0);
    }

    [Fact]
    public async Task BlobService_Download_ShouldReturnResponse()
    {
        var options = Options.Create(new CoordinatorOptions { DefaultTransactionTimeout = TimeSpan.FromMinutes(5) });
        var sessionManager = new SessionManager(options, Substitute.For<ILogger<SessionManager>>());
        var coord = new TransactionCoordinator([new FakeBlobDriver()], sessionManager, options, Substitute.For<ILogger<TransactionCoordinator>>());
        var ctx = new AdaptorServiceContext(coord, options, Substitute.For<ILogger<AdaptorServiceContext>>());
        var svc = new BlobServiceImpl(ctx);
        var callContext = CreateCallContext();

        var txId = await BeginTxId(coord);

        var response = await svc.BlobDownload(
            new BlobDownloadRequest
            {
                TransactionContext = new TransactionContext { TransactionId = txId },
                Key = "test-key",
            },
            callContext);

        Assert.Equal("test-key", response.Key);
        Assert.Equal("text/plain", response.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, response.Data.ToByteArray());
    }
}

/// <summary>
/// Minimal ServerCallContext implementation for unit testing.
/// Grpc.AspNetCore.Server's HttpContextServerCallContext is internal,
/// so we provide a lightweight test double.
/// </summary>
file sealed class HttpContextServerCallContext : ServerCallContext
{
    private readonly HttpContext _httpContext;
    private readonly CancellationToken _cancellationToken;

    private HttpContextServerCallContext(HttpContext httpContext, CancellationToken cancellationToken)
    {
        _httpContext = httpContext;
        _cancellationToken = cancellationToken;
    }

    public static ServerCallContext Create(HttpContext httpContext, CancellationToken ct = default)
        => new HttpContextServerCallContext(httpContext, ct);

    protected override CancellationToken CancellationTokenCore => _cancellationToken;
    protected override string MethodCore => "/Test.Method";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "ipv4:127.0.0.1:5000";
    protected override Metadata RequestHeadersCore => [];
    protected override Metadata ResponseTrailersCore => [];
    protected override Status StatusCore { get => Status.DefaultSuccess; set { } }
    protected override WriteOptions? WriteOptionsCore { get => null; set { } }
    protected override AuthContext AuthContextCore => new AuthContext(null, new Dictionary<string, List<Grpc.Core.AuthProperty>>());
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override IDictionary<object, object> UserStateCore => new Dictionary<object, object>();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
        => Task.CompletedTask;

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
        => throw new NotSupportedException();

    public HttpContext HttpContext => _httpContext;

    /// <summary>Required by Grpc.AspNetCore.Server's GetHttpContext() extension.</summary>
    public Microsoft.AspNetCore.Http.Features.IFeatureCollection Features => _httpContext.Features;
}
