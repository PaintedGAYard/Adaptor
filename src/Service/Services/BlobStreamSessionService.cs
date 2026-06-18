using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Adaptor.Coordinator.Configuration;
using Adaptor.Service.BlobStream;

namespace Adaptor.Service.Services;

/// <summary>
/// gRPC BlobStream control plane: session negotiation and lifecycle management.
/// </summary>
/// <remarks>
/// The WebSocket data plane is handled separately via <c>/blob-stream</c> endpoint.
/// </remarks>
public sealed class BlobStreamSessionServiceImpl : BlobStreamControl.BlobStreamControlBase
{
    private readonly BlobStreamSessionStore _sessionStore;
    private readonly CoordinatorOptions _options;
    private readonly ILogger<BlobStreamSessionServiceImpl> _logger;

    private const uint DefaultChunkSize = 64 * 1024;
    private const uint MaxChunkSize = 4 * 1024 * 1024;

    public BlobStreamSessionServiceImpl(
        BlobStreamSessionStore sessionStore,
        IOptions<CoordinatorOptions> options,
        ILogger<BlobStreamSessionServiceImpl> logger)
    {
        _sessionStore = sessionStore;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Begin a BlobStream session.
    /// 1. Authentication (handled by gRPC interceptor)
    /// 2. Parameter negotiation (timeout, chunkSize)
    /// 3. Issue session_token
    /// </summary>
    public override Task<BlobStreamSessionResponse> BeginSession(
        BlobStreamSessionRequest request, ServerCallContext context)
    {
        var timeout = request.SuggestedTimeout?.ToTimeSpan();

        if (timeout == null || timeout > _options.MaxBlobStreamTransactionTimeout)
        {
            timeout = _options.BlobStreamTransactionTimeout;
        }
        if (timeout < TimeSpan.FromSeconds(10))
        {
            timeout = TimeSpan.FromSeconds(10);
        }

        var chunkSize = request.SuggestedChunkSize;
        if (chunkSize == 0 || chunkSize > MaxChunkSize)
        {
            chunkSize = DefaultChunkSize;
        }

        var entry = _sessionStore.Create(timeout.Value, (int)chunkSize,
            request.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

        _logger.LogInformation(
            "BlobStream session created: token={Token}, timeout={Timeout}, chunkSize={ChunkSize}",
            entry.Token, timeout, chunkSize);

        var response = new BlobStreamSessionResponse
        {
            SessionToken = entry.Token,
            NegotiatedTimeout = Duration.FromTimeSpan(timeout.Value),
            NegotiatedChunkSize = (uint)chunkSize,
            ExpiresAt = Timestamp.FromDateTime(entry.ExpiresAt.ToUniversalTime()),
            WebsocketEndpoint = "/blob-stream",
        };

        return Task.FromResult(response);
    }

    /// <summary>
    /// End a BlobStream session.
    /// Invalidates the session_token and closes the associated WebSocket connection.
    /// The WebSocket data plane transaction will be automatically rolled back.
    /// </summary>
    public override Task<BlobStreamSessionEndResponse> EndSession(
        BlobStreamSessionEndRequest request, ServerCallContext context)
    {
        var token = request.SessionToken;

        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(new BlobStreamSessionEndResponse
            {
                Success = false,
                ErrorMessage = "session_token is required",
            });
        }

        var existed = _sessionStore.Invalidate(token);

        if (existed)
        {
            _logger.LogInformation("BlobStream session ended by gRPC: token={Token}", token);
            return Task.FromResult(new BlobStreamSessionEndResponse { Success = true });
        }
        else
        {
            _logger.LogWarning("BlobStream session not found for EndSession: token={Token}", token);
            return Task.FromResult(new BlobStreamSessionEndResponse
            {
                Success = false,
                ErrorMessage = "Session not found or already ended",
            });
        }
    }
}
