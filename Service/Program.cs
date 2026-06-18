using Adaptor.Coordinator;
using Adaptor.Driver.Postgre;
using Adaptor.Service.Services;

var builder = WebApplication.CreateBuilder(args);

// ─── Adaptor Coordinator ───────────────────────────────────────────────────
builder.Services.AddAdaptorCoordinator(options =>
{
    // 从配置文件加载或使用默认值
    builder.Configuration.GetSection("Adaptor").Bind(options);
});

// ─── PostgreSQL Driver 三元组 (同库: SQL + Vector + BLOB) ────────────────
var pgConnectionString = builder.Configuration.GetConnectionString("PostgreSQL")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:PostgreSQL is required.");

builder.Services.AddAdaptorPostgreDrivers(pgConnectionString);

// ─── Adaptor Service 基础设施 ─────────────────────────────────────────────
builder.Services.AddSingleton<AdaptorServiceContext>();

// ─── gRPC ──────────────────────────────────────────────────────────────────
builder.Services.AddGrpc();

var app = builder.Build();

// ─── Middleware pipeline ────────────────────────────────────────────────────
app.MapGrpcService<TransactionServiceImpl>();
app.MapGrpcService<RelationalServiceImpl>();
app.MapGrpcService<RelationalVectorServiceImpl>();
app.MapGrpcService<BlobServiceImpl>();

app.MapGet("/", () => "Adaptor gRPC Service — Distributed Transaction Coordinator for heterogeneous storage. " +
    "See proto definitions for available services.");

app.Run();
