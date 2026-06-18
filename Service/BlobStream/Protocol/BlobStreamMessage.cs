using System.Buffers.Binary;
using System.Text;

namespace Adaptor.Service.BlobStream.Protocol;

/// <summary>
/// BlobStream 协议的二进制消息读取器/写入器。
///
/// 消息格式:
/// ┌──────┬────────┬──────────┬──────────────────────────┐
/// │ 1B   │ 4B     │ 4B       │ N-Bytes                  │
/// │ Ver  │ OpCode │ BodyLen  │ Body (opcode-specific)   │
/// │ =0x01│ (uint) │ (uint BE)│                          │
/// └──────┴────────┴──────────┴──────────────────────────┘
/// 所有多字节整数均为大端序。
/// </summary>
internal static class BlobStreamMessage
{
    // ─── 读取器 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 从完整消息缓冲区解析头部信息。
    /// </summary>
    public static (uint OpCode, uint BodyLength) ParseHeader(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < OpCode.HeaderSize)
            throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidBody,
                $"Message too small: {buffer.Length} < {OpCode.HeaderSize}");

        byte version = buffer[0];
        if (version != OpCode.ProtocolVersion)
            throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidBody,
                $"Unsupported protocol version: {version}");

        var opCode = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(1, 4));
        var bodyLen = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(5, 4));

        return (opCode, bodyLen);
    }

    // ─── Handshake ───────────────────────────────────────────────────────

    /// <summary>
    /// 构建握手响应（WebSocket 连接建立后首条消息）。
    /// Body: [4B txIdLen][UTF8 txId][8B expiresAtUnixMs]
    /// </summary>
    public static byte[] BuildHandshakeResponse(string txId, DateTime expiresAt)
    {
        var txIdBytes = Encoding.UTF8.GetBytes(txId);
        var unixMs = new DateTimeOffset(expiresAt.ToUniversalTime()).ToUnixTimeMilliseconds();
        var body = new byte[4 + txIdBytes.Length + 8];
        var offset = 0;
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(offset, 4), txIdBytes.Length);
        offset += 4;
        txIdBytes.CopyTo(body, offset);
        offset += txIdBytes.Length;
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(offset, 8), unixMs);
        return BuildResponse(OpCode.Handshake, body);
    }

    // ─── Open ────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析 Open 请求体。
    /// Body 格式: [4B keyLen][UTF8 key][1B mode]
    /// （tx_id 已从协议中移除——WebSocket 事务与连接 1:1 绑定）
    /// </summary>
    public static (string Key, byte Mode) ParseOpenBody(ReadOnlySpan<byte> body)
    {
        var offset = 0;

        var keyLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4));
        offset += 4;
        var key = Encoding.UTF8.GetString(body.Slice(offset, keyLen));
        offset += keyLen;

        var mode = body[offset];

        return (key, mode);
    }

    // ─── Close ───────────────────────────────────────────────────────────

    public static long ParseHandleBody(ReadOnlySpan<byte> body)
    {
        return (long)BinaryPrimitives.ReadUInt64BigEndian(body);
    }

    // ─── Read ────────────────────────────────────────────────────────────

    public static (long Handle, int Count) ParseReadBody(ReadOnlySpan<byte> body)
    {
        var handle = (long)BinaryPrimitives.ReadUInt64BigEndian(body);
        var count = BinaryPrimitives.ReadInt32BigEndian(body.Slice(8, 4));
        return (handle, count);
    }

    // ─── Write ───────────────────────────────────────────────────────────

    public static (long Handle, byte[] Data) ParseWriteBody(ReadOnlySpan<byte> body)
    {
        var handle = (long)BinaryPrimitives.ReadUInt64BigEndian(body);
        var dataLen = BinaryPrimitives.ReadInt32BigEndian(body.Slice(8, 4));
        var data = body.Slice(12, dataLen).ToArray();
        return (handle, data);
    }

    // ─── Seek ────────────────────────────────────────────────────────────

    public static (long Handle, long Offset, SeekOrigin Origin) ParseSeekBody(ReadOnlySpan<byte> body)
    {
        var handle = (long)BinaryPrimitives.ReadUInt64BigEndian(body);
        var offset = (long)BinaryPrimitives.ReadUInt64BigEndian(body.Slice(8, 8));
        var whence = body[16];
        var origin = whence switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => throw new BlobStreamProtocolException(BlobStreamErrorCode.InvalidBody,
                $"Invalid SeekOrigin: {whence}"),
        };
        return (handle, offset, origin);
    }

    // ─── Truncate ────────────────────────────────────────────────────────

    public static (long Handle, long NewLength) ParseTruncateBody(ReadOnlySpan<byte> body)
    {
        var handle = (long)BinaryPrimitives.ReadUInt64BigEndian(body);
        var newLength = (long)BinaryPrimitives.ReadUInt64BigEndian(body.Slice(8, 8));
        return (handle, newLength);
    }

    // ─── 写入器 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 构造响应消息字节数组。
    /// </summary>
    public static byte[] BuildResponse(uint opCode, byte[] body)
    {
        var header = new byte[OpCode.HeaderSize];
        header[0] = OpCode.ProtocolVersion;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1, 4), opCode);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), (uint)body.Length);

        var result = new byte[OpCode.HeaderSize + body.Length];
        header.CopyTo(result, 0);
        body.CopyTo(result, OpCode.HeaderSize);
        return result;
    }

    /// <summary>构造 Open 响应 (handle)</summary>
    public static byte[] BuildOpenResponse(long handle)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(body, (ulong)handle);
        return BuildResponse(OpCode.Open, body);
    }

    /// <summary>构造 Close/Truncate/Commit 响应 (1B status = 0)</summary>
    public static byte[] BuildAckResponse(uint opCode)
    {
        return BuildResponse(opCode, [0]);
    }

    /// <summary>构造 Read 响应 (data)</summary>
    public static byte[] BuildReadResponse(byte[] data)
    {
        var body = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), data.Length);
        data.CopyTo(body, 4);
        return BuildResponse(OpCode.Read, body);
    }

    /// <summary>构造 Write 响应 (bytesWritten)</summary>
    public static byte[] BuildWriteResponse(int bytesWritten)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(body, bytesWritten);
        return BuildResponse(OpCode.Write, body);
    }

    /// <summary>构造 Seek 响应 (newPosition)</summary>
    public static byte[] BuildSeekResponse(long newPosition)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(body, (ulong)newPosition);
        return BuildResponse(OpCode.Seek, body);
    }

    /// <summary>构造错误响应</summary>
    public static byte[] BuildErrorResponse(BlobStreamErrorCode errorCode, string message)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var body = new byte[4 + 2 + msgBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)errorCode);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4, 2), (ushort)msgBytes.Length);
        msgBytes.CopyTo(body, 6);
        return BuildResponse(OpCode.Error, body);
    }

    /// <summary>构造错误响应的快捷方法</summary>
    public static byte[] BuildErrorResponse(BlobStreamProtocolException ex)
        => BuildErrorResponse(ex.ErrorCode, ex.Message);
}
