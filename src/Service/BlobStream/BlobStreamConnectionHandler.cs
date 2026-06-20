using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Adaptor.Coordinator.Abstractions;
using Adaptor.Coordinator.Models;
using Adaptor.Coordinator.Services;
using Adaptor.Service.BlobStream.Protocol;
// Alias to avoid conflict with Adaptor.Service.Transaction (gRPC base class)
using TxTransaction = System.Transactions.Transaction;
using TxStatus = System.Transactions.TransactionStatus;

namespace Adaptor.Service.BlobStream;

/// <summary>
/// WebSocket connection handler for BlobStream (1:1 binding: one connection, one transaction).
/// </summary>
/// <remarks>
/// Design principles:
/// 1. Transaction auto-created on handshake, auto-rolled back on disconnect (or paused).
/// 2. Binary WebSocket messages carry no tx_id — it is implicitly bound to the connection.
/// 3. Non-graceful WS disconnect → pause (keep transaction); Graceful close → terminate (rollback).
/// 4. Reconnection with same session token resumes the paused transaction.
/// </remarks>
internal sealed class BlobStreamConnectionHandler
{
    private readonly TransactionCoordinator _coordinator;
    private readonly HandleManager _handleManager;
    private readonly BlobStreamSessionStore _sessionStore;
    private readonly ILogger<BlobStreamConnectionHandler> _logger;

    private const int InitialBufferSize = 16 * 1024;
    private const int MaxMessageSize = 32 * 1024 * 1024;
    private const int MaxReadSize = 4 * 1024 * 1024;

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
    /// Run the WebSocket connection lifecycle (validate, handshake, message loop, cleanup).
    /// </summary>
    /// <remarks>
    /// 1. Validate session_token (issued by gRPC BeginSession)
    /// 2. Attempt reconnection if session has a paused transaction; otherwise create a new transaction.
    /// 3. Send handshake (with reconnected flag)
    /// 4. Enter message dispatch loop
    /// 5. On disconnect: graceful close → rollback (terminate); non-graceful → pause (keep transaction)
    /// </remarks>
    /// <param name="webSocket">The accepted WebSocket connection.</param>
    /// <param name="connectionId">Connection identifier for logging and session tracking.</param>
    /// <param name="sessionToken">Token issued by <c>BeginSession</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task HandleAsync(WebSocket webSocket, string connectionId, string sessionToken, CancellationToken ct)
    {
        var sessionEntry = _sessionStore.Validate(sessionToken);
        if (sessionEntry == null)
        {
            _logger.LogWarning("Invalid session_token for WS {ConnectionId}: {Token}",
                connectionId, sessionToken);
            await CloseWebSocketAsync(webSocket, ServerShutdownStatus, "Invalid session token");
            return;
        }

        // ── Step 1: Try reconnect or create new transaction ──
        string transactionId = string.Empty;
        CancellationTokenSource? txTimeoutCts = null;
        BeginTransactionResult? beginResult = null;
        bool isReconnect = false;

        // Attempt reconnection if the session has a stored transaction and no active connection.
        if (sessionEntry.TransactionId != null && !sessionEntry.IsConnected)
        {
            var reconnected = _sessionStore.TryReconnect(sessionToken, connectionId);
            if (reconnected != null)
            {
                // Verify the transaction still exists and is active.
                var existingTx = _coordinator.FindTransaction(sessionEntry.TransactionId);
                if (existingTx?.TransactionInformation.Status == TxStatus.Active)
                {
                    transactionId = sessionEntry.TransactionId;
                    isReconnect = true;
                    _logger.LogInformation(
                        "BlobStream WS {ConnectionId} reconnected to session {Token}, tx={TxId} (reconnect #{Count})",
                        connectionId, sessionToken, transactionId, sessionEntry.ReconnectCount);
                }
                else
                {
                    // Transaction is gone or not active — treat as new connection.
                    _sessionStore.Invalidate(sessionToken);
                    sessionEntry = _sessionStore.Validate(sessionToken);
                    if (sessionEntry == null)
                    {
                        await CloseWebSocketAsync(webSocket, ServerShutdownStatus, "Session invalidated");
                        return;
                    }
                    isReconnect = false;
                }
            }
            else
            {
                // Reconnect refused (pause timeout or session expired).
                await CloseWebSocketAsync(webSocket, ServerShutdownStatus, "Session expired or paused too long");
                return;
            }
        }
        else
        {
            isReconnect = false;
        }

        if (!isReconnect)
        {
            try
            {
                beginResult = await _coordinator.BeginBlobStreamTransactionAsync(
                    sessionEntry.Parameters.NegotiatedTimeout,
                    connectionId: connectionId, ct: ct);
                transactionId = beginResult.Transaction.TransactionInformation.LocalIdentifier;
                sessionEntry.TransactionId = transactionId;

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
        }

        sessionEntry.ConnectionId = connectionId;

        // ── Step 2: Set up cancellation sources ──
        using var sessionCancelCts = new CancellationTokenSource();
        sessionEntry.WsCancellation = sessionCancelCts;
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
        var expiresAt = isReconnect
            ? _coordinator.FindTransaction(transactionId)?.TransactionInformation.CreationTime
                  + TimeSpan.FromHours(24) // Fallback expiry for reconnected sessions
                  ?? DateTime.UtcNow.AddHours(24)
            : beginResult!.ExpiresAt;

        _logger.LogInformation(
            "BlobStream WS {ConnectionId} {Action}, tx={TxId}, reconnect={IsReconnect}",
            connectionId, isReconnect ? "reconnected" : "opened", transactionId, isReconnect);

        // ── Step 3: Send handshake ──
        try
        {
            var handshake = BlobStreamMessage.BuildHandshakeResponse(transactionId, expiresAt, isReconnect);
            await webSocket.SendAsync(
                new ArraySegment<byte>(handshake),
                WebSocketMessageType.Binary, endOfMessage: true, linkedToken);
        }
        catch (OperationCanceledException) when (sessionCancelCts.IsCancellationRequested)
        {
            await CleanupTransactionAsync(connectionId, transactionId);
            return;
        }
        catch (Exception) when (webSocket.State != WebSocketState.Open)
        {
            // WS closed before handshake completed — pause (don't rollback).
            _sessionStore.MarkPaused(sessionToken);
            return;
        }

        // ── Step 4: Message loop ──
        bool clientInitiatedClose = false;
        bool abnormalClose = false;
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
                    // Timeout or EndSession → terminate (rollback).
                    break;
                }
                catch (WebSocketException)
                {
                    // Non-graceful disconnect → pause.
                    abnormalClose = true;
                    break;
                }
                catch (Exception)
                {
                    // Non-graceful disconnect → pause.
                    abnormalClose = true;
                    break;
                }

                if (message == null)
                {
                    // Client initiated graceful close → terminate (rollback).
                    clientInitiatedClose = true;
                    break;
                }

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

        // ── Step 5: Cleanup — pause vs terminate ──
        if (abnormalClose)
        {
            // Non-graceful disconnect: clean handles only, keep transaction (pause).
            _logger.LogInformation(
                "BlobStream WS {ConnectionId} disconnected abnormally — pausing tx {TxId}",
                connectionId, transactionId);
            await CleanupConnectionAsync(connectionId);
            _sessionStore.MarkPaused(sessionToken);
        }
        else
        {
            // Graceful close, timeout, or EndSession: rollback transaction (terminate).
            await CleanupConnectionAsync(connectionId);
            await CleanupTransactionAsync(connectionId, transactionId);
        }

        _logger.LogInformation("BlobStream WS closed: {ConnectionId}", connectionId);
    }

    /// <summary>
    /// Receive a complete message from WebSocket, handling frame reassembly.
    /// </summary>
    /// <returns>Complete message bytes, or <c>null</c> if the peer initiated a close.</returns>
    private static async Task<byte[]?> ReceiveFullMessageAsync(
        WebSocket webSocket, byte[] initialBuffer, CancellationToken ct)
    {
        using var ms = new MemoryStream(initialBuffer.Length);
        var buffer = initialBuffer;

        WebSocketReceiveResult result;
        do
        {
            // Ensure buffer is sufficient
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

            // If there is more data, expand buffer
            if (!result.EndOfMessage && ms.Length + buffer.Length > MaxMessageSize)
            {
                throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidBody,
                    $"Message exceeds maximum size of {MaxMessageSize} bytes");
            }
        }
        while (!result.EndOfMessage);

        return ms.ToArray();
    }

    #region Request dispatch

    private async Task<byte[]> ProcessRequestAsync(
        byte[] message, string transactionId, string connectionId, CancellationToken ct)
    {
        var (opCode, _) = BlobStreamMessage.ParseHeader(message);

        _logger.LogTrace(
            "BlobStream WS {ConnectionId} opcode=0x{OpCode:X2} ({OpName})",
            connectionId, (byte)opCode, opCode);

        return opCode switch
        {
            OpCode.Open => await HandleOpenAsync(message, transactionId, connectionId, ct),
            OpCode.Close => await HandleCloseAsync(message, transactionId, ct),
            OpCode.Read => await HandleReadAsync(message, transactionId, ct),
            OpCode.Write => await HandleWriteAsync(message, transactionId, ct),
            OpCode.Seek => await HandleSeekAsync(message, transactionId, ct),
            OpCode.Truncate => await HandleTruncateAsync(message, transactionId, ct),
            _ => throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidOpCode,
                $"Unknown opcode: 0x{opCode:X2}"),
        };
    }

    #endregion

    #region Open

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

    #endregion

    #region Close

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
            // After commit the transaction is complete; lo_close is handled by PG connection release
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

    #endregion

    #region Read

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

    #endregion

    #region Write

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

    #endregion

    #region Seek

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

    #endregion

    #region Truncate

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

    #endregion

    #region Driver execution

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

    #endregion

    #region Handle validation

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

    #endregion

    #region Cleanup

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
        catch { }
    }

    #endregion
}
