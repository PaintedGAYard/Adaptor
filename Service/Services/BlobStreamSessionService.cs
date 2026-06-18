using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Adaptor.Coordinator.Configuration;
using Adaptor.Service.BlobStream;

namespace Adaptor.Service.Services;

/// <summary>
/// gRPC BlobStream service 实现（控制面）。
///
/// 职责:
/// 1. BeginSession — 身份验证（通过 gRPC 拦截器）、参数协商、签发 session_token
/// 2. EndSession — 失效 token、关闭关联 WebSocket 连接
///
/// WebSocket 数据面不在此服务中——它使用 session_token 在 /blob-stream 端点独立连接。
/// </summary>
public sealed class BlobStreamSessionServiceImpl : BlobStreamControl.BlobStreamControlBase
{
    private readonly BlobStreamSessionStore _sessionStore;
    private readonly CoordinatorOptions _options;
    private readonly ILogger<BlobStreamSessionServiceImpl> _logger;

    // 默认 chunk 大小: 64 KiB
    private const uint DefaultChunkSize = 64 * 1024;
    private const uint MaxChunkSize = 4 * 1024 * 1024; // 4 MiB

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
    /// 开始一个 BlobStream 会话。
    /// 1. 验证身份（由 gRPC 拦截器处理，此处获取已验证的上下文）
    /// 2. 协商参数（timeout, chunkSize）
    /// 3. 签发 session_token
    /// </summary>
    public override Task<BlobStreamSessionResponse> BeginSession(
        BlobStreamSessionRequest request, ServerCallContext context)
    {
        // ── 参数协商 ──
        var timeout = request.SuggestedTimeout?.ToTimeSpan();

        // 服务端决定最终超时（不能超过最大限制）
        if (timeout == null || timeout > _options.MaxBlobStreamTransactionTimeout)
        {
            timeout = _options.BlobStreamTransactionTimeout;
        }
        if (timeout < TimeSpan.FromSeconds(10))
        {
            timeout = TimeSpan.FromSeconds(10); // 最小 10 秒
        }

        // 协商 chunk 大小
        var chunkSize = request.SuggestedChunkSize;
        if (chunkSize == 0 || chunkSize > MaxChunkSize)
        {
            chunkSize = DefaultChunkSize;
        }

        // ── 签发 token ──
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
    /// 结束一个 BlobStream 会话。
    /// 失效 session_token，关闭关联的 WebSocket 连接（如有）。
    /// WebSocket 数据面的事务将自动回滚。
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
