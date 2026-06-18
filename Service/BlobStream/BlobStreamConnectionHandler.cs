using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Models;
using Adaptor.Coordinator.Services;
using Adaptor.Service.BlobStream.Protocol;
// 别名：避免与 Adaptor.Service.Transaction (gRPC 基类) 冲突
using TxTransaction = System.Transactions.Transaction;
using TxStatus = System.Transactions.TransactionStatus;

namespace Adaptor.Service.BlobStream;

/// <summary>
/// BlobStream WebSocket 连接处理器。
///
/// ═══ 核心设计原则 ═══
///
/// 1. 严格 1:1 绑定
///    每个 WebSocket 连接拥有一个专用的 Blob Stream 事务（长超时）。
///    事务在 WebSocket 握手时自动创建，连接关闭时自动回滚（若未提交）。
///    gRPC CommitTransaction 可通过 tx_id 提交此事务。
///
/// 2. 事务生命周期 = WebSocket 生命周期
///    - 连接建立 → 事务开始
///    - 事务超时 → 服务端主动关闭 WebSocket + 发送超时通知
///    - 连接关闭 → 事务回滚（若仍 Active）
///
/// 3. 协议独立性
///    WebSocket 二进制消息中不携带 tx_id——事务由连接上下文隐式确定。
///    tx_id 仅在握手响应中返回，供 gRPC Commit 使用。
///
/// ═══════════════════════════════════════════════════
/// </summary>
internal sealed class BlobStreamConnectionHandler
{
    private readonly TransactionCoordinator _coordinator;
    private readonly HandleManager _handleManager;
    private readonly BlobStreamSessionStore _sessionStore;
    private readonly ILogger<BlobStreamConnectionHandler> _logger;

    private const int InitialBufferSize = 16 * 1024;
    private const int MaxMessageSize = 32 * 1024 * 1024;  // 32 MB
    private const int MaxReadSize = 4 * 1024 * 1024;      // 4 MiB

    // 关闭状态码（1000+ 为应用层自定义）
    private const WebSocketCloseStatus TimeoutStatus = (WebSocketCloseStatus)4001;
    private const WebSocketCloseStatus TransactionEndedStatus = (WebSocketCloseStatus)4002;
    private const WebSocketCloseStatus ServerShutdownStatus = (WebSocketCloseStatus)4003;
    private const WebSocketCloseStatus SessionInvalidatedStatus = (WebSocketCloseStatus)4004;

    public BlobStreamConnectionHandler(
        TransactionCoordinator coordinator,
        HandleManager handleManager,
        BlobStreamSessionStore sessionStore,
        ILogger<BlobStreamConnectionHandler> logger)
    {
        _coordinator = coordinator;
        _handleManager = handleManager;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    /// <summary>
    /// 处理 WebSocket 连接生命周期。
    /// 1. 验证 session_token（由 gRPC BeginSession 签发）
    /// 2. 加载协商参数，创建专用 Blob Stream 事务
    /// 3. 注册到 session（支持 gRPC EndSession 主动关闭）
    /// 4. 发送握手消息
    /// 5. 进入消息循环
    /// 6. 连接断开时自动回滚事务（若未提交）
    /// </summary>
    public async Task HandleAsync(WebSocket webSocket, string connectionId, string sessionToken, CancellationToken ct)
    {
        // ── Step 1: 验证 session_token ─────────────────────────────────
        var sessionEntry = _sessionStore.Validate(sessionToken);
        if (sessionEntry == null)
        {
            _logger.LogWarning("Invalid session_token for WS {ConnectionId}: {Token}",
                connectionId, sessionToken);
            await CloseWebSocketAsync(webSocket, ServerShutdownStatus, "Invalid session token");
            return;
        }

        // ── Step 2: 创建专用 Blob Stream 事务（使用协商参数）───────────
        BeginTransactionResult beginResult;
        string transactionId;
        CancellationTokenSource? txTimeoutCts = null;

        try
        {
            beginResult = await _coordinator.BeginBlobStreamTransactionAsync(
                sessionEntry.Parameters.NegotiatedTimeout,
                connectionId: connectionId, ct: ct);
            transactionId = beginResult.Transaction.TransactionInformation.LocalIdentifier;

            var txTimeout = beginResult.ExpiresAt - DateTime.UtcNow;
            if (txTimeout > TimeSpan.Zero)
            {
                txTimeoutCts = new CancellationTokenSource(txTimeout);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create BlobStream tx for {ConnectionId}", connectionId);
            await CloseWebSocketAsync(webSocket, ServerShutdownStatus, "Failed to create transaction");
            return;
        }

        // ── Step 3: 注册到 session（支持 gRPC EndSession 主动关闭）────
        using var sessionCancelCts = new CancellationTokenSource();
        sessionEntry.WsCancellation = sessionCancelCts;
        sessionEntry.ConnectionId = connectionId;

        // ── Step 4: 超时 / 外部取消 / 服务端关闭 → 主动关闭 WS ──────
        using var timeoutCts = txTimeoutCts;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct, sessionCancelCts.Token);
        if (txTimeoutCts != null)
        {
            txTimeoutCts.Token.Register(() =>
            {
                _logger.LogWarning("BlobStream tx {TxId} timeout, closing WS {ConnectionId}",
                    transactionId, connectionId);
                _ = CloseWebSocketAsync(webSocket, TimeoutStatus, "Transaction timeout");
            });
        }
        // gRPC EndSession 触发 → 关闭 WS
        sessionCancelCts.Token.Register(() =>
        {
            if (webSocket.State == WebSocketState.Open)
            {
                _logger.LogInformation(
                    "gRPC EndSession for token {Token}, closing WS {ConnectionId}",
                    sessionToken, connectionId);
                _ = CloseWebSocketAsync(webSocket, SessionInvalidatedStatus, "Session ended by server");
            }
        });

        var linkedToken = linkedCts.Token;
        _logger.LogInformation("BlobStream WS {ConnectionId} opened, tx={TxId}, timeout={Timeout}",
            connectionId, transactionId, beginResult.ExpiresAt - DateTime.UtcNow);

        // ── Step 5: 发送握手（唯一一次暴露 tx_id）─────────────────────
        try
        {
            var handshake = BlobStreamMessage.BuildHandshakeResponse(transactionId, beginResult.ExpiresAt);
            await webSocket.SendAsync(
                new ArraySegment<byte>(handshake),
                WebSocketMessageType.Binary, endOfMessage: true, linkedToken);
        }
        catch (OperationCanceledException) when (sessionCancelCts.IsCancellationRequested)
        {
            // gRPC EndSession 在握手完成前触发——无需额外操作
            await CleanupTransactionAsync(connectionId, transactionId);
            return;
        }
        catch (Exception) when (webSocket.State != WebSocketState.Open)
        {
            await CleanupTransactionAsync(connectionId, transactionId);
            return;
        }

        // ── Step 6: 消息循环 ──────────────────────────────────────────
        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        try
        {
            while (webSocket.State == WebSocketState.Open && !linkedToken.IsCancellationRequested)
            {
                byte[]? message;
                try
                {
                    message = await ReceiveFullMessageAsync(webSocket, buffer, linkedToken);
                }
                catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
                {
                    // 事务超时 / gRPC EndSession → Close 已在 Register 中触发
                    break;
                }

                if (message == null) break; // 客户端主动关闭

                byte[] response;
                bool shouldClose = false;
                try
                {
                    response = await ProcessRequestAsync(message, transactionId, connectionId, linkedToken);
                }
                catch (BlobStreamProtocolException ex)
                {
                    _logger.LogWarning(ex, "Protocol error on {ConnectionId}", connectionId);
                    response = BlobStreamMessage.BuildErrorResponse(ex);

                    if (ex.ErrorCode == BlobStreamErrorCode.TransactionNotActive ||
                        ex.ErrorCode == BlobStreamErrorCode.TransactionNotFound)
                    {
                        shouldClose = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Internal error on {ConnectionId}", connectionId);
                    response = BlobStreamMessage.BuildErrorResponse(
                        BlobStreamErrorCode.UnknownError, ex.Message);
                }

                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(response),
                        WebSocketMessageType.Binary, endOfMessage: true, linkedToken);

                    if (shouldClose)
                    {
                        await CloseWebSocketAsync(webSocket, TransactionEndedStatus, "Transaction completed");
                        break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // ── Step 7: 清理 ──────────────────────────────────────────────
        await CleanupConnectionAsync(connectionId);
        await CleanupTransactionAsync(connectionId, transactionId);
        _logger.LogInformation("BlobStream WS closed: {ConnectionId}", connectionId);
    }

    /// <summary>
    /// 从 WebSocket 接收一条完整消息（处理分帧）。
    /// 不使用 ref 参数（async 方法不支持 ref），使用 MemoryStream 内部管理。
    /// </summary>
    private static async Task<byte[]?> ReceiveFullMessageAsync(
        WebSocket webSocket, byte[] initialBuffer, CancellationToken ct)
    {
        using var ms = new MemoryStream(initialBuffer.Length);
        var buffer = initialBuffer;

        WebSocketReceiveResult result;
        do
        {
            // 确保缓冲区足够
            if (ms.Position + buffer.Length > MaxMessageSize)
                throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidBody,
                    $"Message exceeds maximum size of {MaxMessageSize} bytes");

            result = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer, 0, buffer.Length),
                ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "Closing", ct);
                }
                return null;
            }

            await ms.WriteAsync(buffer, 0, result.Count, ct);

            // 如果还有更多数据，扩容缓冲区
            if (!result.EndOfMessage && ms.Length + buffer.Length > MaxMessageSize)
            {
                throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidBody,
                    $"Message exceeds maximum size of {MaxMessageSize} bytes");
            }
        }
        while (!result.EndOfMessage);

        return ms.ToArray();
    }

    // ─── 请求分发 ─────────────────────────────────────────────────────────

    private async Task<byte[]> ProcessRequestAsync(
        byte[] message, string transactionId, string connectionId, CancellationToken ct)
    {
        var (opCode, _) = BlobStreamMessage.ParseHeader(message);

        return opCode switch
        {
            OpCode.Open => await HandleOpenAsync(message, transactionId, connectionId, ct),
            OpCode.Close => await HandleCloseAsync(message, transactionId, ct),
            OpCode.Read => await HandleReadAsync(message, transactionId, ct),
            OpCode.Write => await HandleWriteAsync(message, transactionId, ct),
            OpCode.Seek => await HandleSeekAsync(message, transactionId, ct),
            OpCode.Truncate => await HandleTruncateAsync(message, transactionId, ct),
            OpCode.Commit => await HandleCommitAsync(transactionId, ct),
            _ => throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidOpCode,
                $"Unknown opcode: 0x{opCode:X2}"),
        };
    }

    // ─── Open ─────────────────────────────────────────────────────────────

    private async Task<byte[]> HandleOpenAsync(
        byte[] message, string transactionId, string connectionId, CancellationToken ct)
    {
        var body = message.AsSpan(OpCode.HeaderSize);
        var (key, modeByte) = BlobStreamMessage.ParseOpenBody(body);
        var mode = (BlobAccessMode)modeByte;

        BlobOpenResult result;
        try
        {
            result = await _coordinator.ExecuteOnCapabilityAsync<IBlobRandomAccessCapability, BlobOpenResult>(
                transactionId,
                async (driver, tx) => await driver.OpenAsync(key, mode, tx, ct),
                ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            throw new BlobStreamProtocolException(BlobStreamErrorCode.TransactionNotFound, ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No driver"))
        {
            throw new BlobStreamProtocolException(BlobStreamErrorCode.DriverNotAvailable, ex.Message);
        }

        if (result.ErrorMessage != null)
        {
            var errorCode = result.ErrorMessage switch
            {
                var m when m.Contains("not found") => BlobStreamErrorCode.KeyNotFound,
                var m when m.Contains("already exists") => BlobStreamErrorCode.KeyAlreadyExists,
                _ => BlobStreamErrorCode.IoError,
            };
            throw new BlobStreamProtocolException(errorCode, result.ErrorMessage);
        }

        var handleId = _handleManager.Register(connectionId, result.LoFd, key, transactionId);
        _logger.LogDebug("Open: key={Key}, mode={Mode}, fd={LoFd}, handle={HandleId}",
            key, mode, result.LoFd, handleId);

        return BlobStreamMessage.BuildOpenResponse(handleId);
    }

    // ─── Close ────────────────────────────────────────────────────────────

    private async Task<byte[]> HandleCloseAsync(byte[] message, string transactionId, CancellationToken ct)
    {
        var body = message.AsSpan(OpCode.HeaderSize);
        var handleId = BlobStreamMessage.ParseHandleBody(body);

        if (!_handleManager.Remove(handleId, out var entry) || entry == null)
        {
            throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidHandle,
                $"Handle {handleId} not found or already closed");
        }

        await entry.Gate.WaitAsync(ct);
        try
        {
            // Commit 后事务已结束，lo_close 由 PG 连接释放自动处理
            var tx = _coordinator.FindTransaction(transactionId);
            if (tx?.TransactionInformation.Status == TxStatus.Active)
            {
                await ExecuteOnDriverAsync(
                    (driver, t) => driver.CloseAsync(entry.LoFd, t, ct),
                    transactionId, ct);
            }
        }
        catch (BlobStreamProtocolException ex) when (
            ex.ErrorCode == BlobStreamErrorCode.TransactionNotActive ||
            ex.ErrorCode == BlobStreamErrorCode.TransactionNotFound)
        {
            // 事务已结束——close 操作本身仍成功
            _logger.LogDebug("Close: handle={HandleId}, key={Key} (tx already ended)", handleId, entry.Key);
        }
        finally
        {
            entry.Gate.Release();
            entry.Dispose();
        }

        _logger.LogDebug("Close: handle={HandleId}, key={Key}", handleId, entry.Key);
        return BlobStreamMessage.BuildAckResponse(OpCode.Close);
    }

    // ─── Read ─────────────────────────────────────────────────────────────

    private async Task<byte[]> HandleReadAsync(byte[] message, string transactionId, CancellationToken ct)
    {
        var body = message.AsSpan(OpCode.HeaderSize);
        var (handleId, count) = BlobStreamMessage.ParseReadBody(body);

        if (count <= 0 || count > MaxReadSize)
            throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidBody,
                $"Read count must be between 1 and {MaxReadSize}, got {count}");

        var entry = GetValidatedEntry(handleId, transactionId);
        await entry.Gate.WaitAsync(ct);
        try
        {
            var result = await ExecuteOnDriverAsync(
                (driver, tx) => driver.ReadAsync(entry.LoFd, count, tx, ct),
                transactionId, ct);

            return BlobStreamMessage.BuildReadResponse(result.Data);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // ─── Write ────────────────────────────────────────────────────────────

    private async Task<byte[]> HandleWriteAsync(byte[] message, string transactionId, CancellationToken ct)
    {
        var body = message.AsSpan(OpCode.HeaderSize);
        var (handleId, data) = BlobStreamMessage.ParseWriteBody(body);

        var entry = GetValidatedEntry(handleId, transactionId);
        await entry.Gate.WaitAsync(ct);
        try
        {
            var bytesWritten = await ExecuteOnDriverAsync(
                (driver, tx) => driver.WriteAsync(entry.LoFd, data, tx, ct),
                transactionId, ct);

            return BlobStreamMessage.BuildWriteResponse(bytesWritten);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // ─── Seek ─────────────────────────────────────────────────────────────

    private async Task<byte[]> HandleSeekAsync(byte[] message, string transactionId, CancellationToken ct)
    {
        var body = message.AsSpan(OpCode.HeaderSize);
        var (handleId, offset, origin) = BlobStreamMessage.ParseSeekBody(body);

        var entry = GetValidatedEntry(handleId, transactionId);
        await entry.Gate.WaitAsync(ct);
        try
        {
            var newPosition = await ExecuteOnDriverAsync(
                (driver, tx) => driver.SeekAsync(entry.LoFd, offset, origin, tx, ct),
                transactionId, ct);

            return BlobStreamMessage.BuildSeekResponse(newPosition);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // ─── Truncate ─────────────────────────────────────────────────────────

    private async Task<byte[]> HandleTruncateAsync(byte[] message, string transactionId, CancellationToken ct)
    {
        var body = message.AsSpan(OpCode.HeaderSize);
        var (handleId, newLength) = BlobStreamMessage.ParseTruncateBody(body);

        var entry = GetValidatedEntry(handleId, transactionId);
        await entry.Gate.WaitAsync(ct);
        try
        {
            await ExecuteOnDriverAsync(
                (driver, tx) => driver.TruncateAsync(entry.LoFd, newLength, tx, ct),
                transactionId, ct);

            return BlobStreamMessage.BuildAckResponse(OpCode.Truncate);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // ─── Commit ──────────────────────────────────────────────────────────

    /// <summary>
    /// 提交事务（一次性最终语义）。
    /// 将当前所有写入持久化并结束分布式事务。
    /// 成功后不可再进行任何数据操作，只能 Close 或关闭 WebSocket。
    /// 不可重复调用——再次 Commit 会收到错误。
    /// </summary>
    private async Task<byte[]> HandleCommitAsync(string transactionId, CancellationToken ct)
    {
        _logger.LogInformation("Commit: committing transaction {TxId}", transactionId);

        var commitResult = await _coordinator.CommitTransactionAsync(transactionId, ct);

        if (commitResult.Status == Coordinator.Models.CommitStatus.Committed)
        {
            _logger.LogInformation("Commit: transaction {TxId} committed successfully", transactionId);
            return BlobStreamMessage.BuildAckResponse(OpCode.Commit);
        }
        else
        {
            var msg = $"Commit failed: status={commitResult.Status}, error={commitResult.ErrorMessage}";
            _logger.LogError(msg);
            throw new BlobStreamProtocolException(BlobStreamErrorCode.IoError, msg);
        }
    }

    // ─── Driver 执行 ─────────────────────────────────────────────────────

    private async Task<TResult> ExecuteOnDriverAsync<TResult>(
        Func<IBlobRandomAccessCapability, TxTransaction, Task<TResult>> operation,
        string transactionId, CancellationToken ct)
    {
        var (driver, tx) = await ResolveDriverAndTransactionAsync(transactionId, ct);
        return await operation(driver, tx);
    }

    private async Task ExecuteOnDriverAsync(
        Func<IBlobRandomAccessCapability, TxTransaction, Task> operation,
        string transactionId, CancellationToken ct)
    {
        var (driver, tx) = await ResolveDriverAndTransactionAsync(transactionId, ct);
        await operation(driver, tx);
    }

    private async Task<(IBlobRandomAccessCapability Driver, TxTransaction Transaction)> ResolveDriverAndTransactionAsync(
        string transactionId, CancellationToken ct)
    {
        var tx = _coordinator.FindTransaction(transactionId)
            ?? throw new BlobStreamProtocolException(BlobStreamErrorCode.TransactionNotFound,
                $"Transaction '{transactionId}' not found");

        var driver = _coordinator.GetDrivers<IResourceManager>()
            .OfType<IBlobRandomAccessCapability>()
            .FirstOrDefault()
            ?? throw new BlobStreamProtocolException(BlobStreamErrorCode.DriverNotAvailable,
                "No driver implementing IBlobRandomAccessCapability registered");

        if (driver is ITransactionalResourceManager txDriver)
            txDriver.Enlist(tx);

        return (driver, tx);
    }

    // ─── Handle 验证 ─────────────────────────────────────────────────────

    private HandleEntry GetValidatedEntry(long handleId, string expectedTransactionId)
    {
        if (!_handleManager.TryGet(handleId, out var entry))
        {
            throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidHandle,
                $"Handle {handleId} not found or already closed");
        }

        if (entry.TransactionId != expectedTransactionId)
        {
            throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidHandle,
                $"Handle {handleId} belongs to a different transaction");
        }

        var tx = _coordinator.FindTransaction(entry.TransactionId);
        if (tx == null)
        {
            _handleManager.Remove(handleId, out _);
            entry.Dispose();
            throw new BlobStreamProtocolException(BlobStreamErrorCode.TransactionNotFound,
                $"Transaction '{entry.TransactionId}' not found for handle {handleId}");
        }

        if (tx.TransactionInformation.Status != TxStatus.Active)
        {
            _handleManager.Remove(handleId, out _);
            entry.Dispose();
            throw new BlobStreamProtocolException(BlobStreamErrorCode.TransactionNotActive,
                $"Transaction '{entry.TransactionId}' is {tx.TransactionInformation.Status}, not Active");
        }

        return entry;
    }

    // ─── 清理 ─────────────────────────────────────────────────────────────

    private async Task CleanupConnectionAsync(string connectionId)
    {
        var handles = _handleManager.RemoveAllForConnection(connectionId);
        foreach (var entry in handles)
        {
            try
            {
                var tx = _coordinator.FindTransaction(entry.TransactionId);
                if (tx?.TransactionInformation.Status == TxStatus.Active)
                {
                    await ExecuteOnDriverAsync(
                        (driver, t) => driver.CloseAsync(entry.LoFd, t, CancellationToken.None),
                        entry.TransactionId, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up handle {HandleId}", entry.HandleId);
            }
            finally
            {
                entry.Dispose();
            }
        }
    }

    private async Task CleanupTransactionAsync(string connectionId, string transactionId)
    {
        var tx = _coordinator.FindTransaction(transactionId);
        if (tx == null) return;

        if (tx.TransactionInformation.Status == TxStatus.Active)
        {
            _logger.LogInformation(
                "Rolling back BlobStream tx {TxId} for connection {ConnectionId}",
                transactionId, connectionId);
            try
            {
                await _coordinator.RollbackTransactionAsync(transactionId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rolling back BlobStream tx {TxId}", transactionId);
            }
        }
    }

    private static async Task CloseWebSocketAsync(
        WebSocket webSocket, WebSocketCloseStatus status, string reason)
    {
        if (webSocket.State != WebSocketState.Open) return;
        try
        {
            await webSocket.CloseAsync(status, reason, CancellationToken.None);
        }
        catch { /* 忽略关闭时的异常 */ }
    }
}
