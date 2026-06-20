using System.Net;
using Adaptor.Coordinator;
using Adaptor.Driver.Postgre;
using Adaptor.Service.Abstractions;
using Adaptor.Service.BlobStream;
using Adaptor.Service.Middleware;
using Adaptor.Service.Services;

var builder = WebApplication.CreateBuilder(args);

#region Adaptor Coordinator
builder.Services.AddAdaptorCoordinator(options =>
{
    builder.Configuration.GetSection("Adaptor").Bind(options);
});

// PostgreSQL Driver trio (same database: SQL + Vector + BLOB)
var pgConnectionString = builder.Configuration.GetConnectionString("PostgreSQL")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:PostgreSQL is required.");

builder.Services.AddAdaptorPostgreDrivers(pgConnectionString);

builder.Services.AddSingleton<AdaptorServiceContext>();

builder.Services.AddSingleton<BlobStreamSessionStore>();
builder.Services.AddSingleton<HandleManager>();
builder.Services.AddSingleton<BlobStreamConnectionHandler>();

// Register the default connection ID provider (uses GetHttpContext)
builder.Services.AddSingleton<IConnectionIdProvider, DefaultConnectionIdProvider>();

builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<GrpcLoggingInterceptor>();
});

builder.Services.Configure<WebSocketOptions>(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

app.UseWebSockets();

#endregion

#region BlobStream WebSocket endpoint
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

#endregion

#region gRPC services
app.MapGrpcService<TransactionServiceImpl>();
app.MapGrpcService<BlobStreamSessionServiceImpl>();
app.MapGrpcService<RelationalServiceImpl>();
app.MapGrpcService<RelationalVectorServiceImpl>();
app.MapGrpcService<BlobServiceImpl>();

app.MapGet("/", () => "Adaptor gRPC Service — Distributed Transaction Coordinator for heterogeneous storage. " +
    "See proto definitions for available services.");

#endregion

app.Run();
