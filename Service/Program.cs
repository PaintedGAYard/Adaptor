using System.Net;
using Adaptor.Coordinator;
using Adaptor.Driver.Postgre;
using Adaptor.Service.BlobStream;
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

// ─── BlobStream 组件 ─────────────────────────────────────────────────────
builder.Services.AddSingleton<BlobStreamSessionStore>();
builder.Services.AddSingleton<HandleManager>();
builder.Services.AddSingleton<BlobStreamConnectionHandler>();

// ─── gRPC ──────────────────────────────────────────────────────────────────
builder.Services.AddGrpc();

// ─── WebSocket ─────────────────────────────────────────────────────────────
builder.Services.Configure<WebSocketOptions>(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// ─── Middleware pipeline ────────────────────────────────────────────────────
app.UseWebSockets();

// BlobStream WebSocket endpoint — 与 gRPC 共享端口
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/blob-stream" && context.WebSockets.IsWebSocketRequest)
    {
        var sessionToken = context.Request.Query["token"].FirstOrDefault();
        if (string.IsNullOrEmpty(sessionToken))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing 'token' query parameter");
            return;
        }

        var connectionId = context.Connection.Id;
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var handler = context.RequestServices.GetRequiredService<BlobStreamConnectionHandler>();
        await handler.HandleAsync(webSocket, connectionId, sessionToken, context.RequestAborted);
    }
    else
    {
        await next();
    }
});

app.MapGrpcService<TransactionServiceImpl>();
app.MapGrpcService<BlobStreamSessionServiceImpl>();
app.MapGrpcService<RelationalServiceImpl>();
app.MapGrpcService<RelationalVectorServiceImpl>();
app.MapGrpcService<BlobServiceImpl>();

app.MapGet("/", () => "Adaptor gRPC Service — Distributed Transaction Coordinator for heterogeneous storage. " +
    "See proto definitions for available services.");

app.Run();
