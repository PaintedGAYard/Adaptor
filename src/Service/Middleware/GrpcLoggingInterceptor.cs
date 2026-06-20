using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace Adaptor.Service.Middleware;

/// <summary>
/// gRPC interceptor that logs every request and response at Trace level,
/// including method name and elapsed time.
/// </summary>
public sealed class GrpcLoggingInterceptor : Interceptor
{
    private readonly ILogger<GrpcLoggingInterceptor> _logger;

    public GrpcLoggingInterceptor(ILogger<GrpcLoggingInterceptor> logger)
    {
        _logger = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var method = context.Method;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogTrace("gRPC >> {Method}", method);

        try
        {
            var response = await continuation(request, context);
            stopwatch.Stop();
            _logger.LogTrace(
                "gRPC << {Method} completed in {ElapsedMs}ms", method, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex,
                "gRPC !! {Method} failed after {ElapsedMs}ms", method, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var method = context.Method;
        _logger.LogTrace("gRPC >> ServerStreaming {Method}", method);

        try
        {
            await continuation(request, responseStream, context);
            _logger.LogTrace("gRPC << ServerStreaming {Method} completed", method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC !! ServerStreaming {Method} failed", method);
            throw;
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var method = context.Method;
        _logger.LogTrace("gRPC >> ClientStreaming {Method}", method);

        try
        {
            var response = await continuation(requestStream, context);
            _logger.LogTrace("gRPC << ClientStreaming {Method} completed", method);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC !! ClientStreaming {Method} failed", method);
            throw;
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var method = context.Method;
        _logger.LogTrace("gRPC >> DuplexStreaming {Method}", method);

        try
        {
            await continuation(requestStream, responseStream, context);
            _logger.LogTrace("gRPC << DuplexStreaming {Method} completed", method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC !! DuplexStreaming {Method} failed", method);
            throw;
        }
    }
}
