using System.Buffers.Binary;
using System.Text;

namespace Adaptor.Service.BlobStream.Protocol;

/// <summary>
/// Binary message reader/writer for the BlobStream protocol.
///
/// Message format:
/// ┌──────┬────────┬──────────┬──────────────────────────┐
/// │ 1B   │ 4B     │ 4B       │ N-Bytes                  │
/// │ Ver  │ OpCode │ BodyLen  │ Body (opcode-specific)   │
/// │ =0x01│ (uint) │ (uint BE)│                          │
/// └──────┴────────┴──────────┴──────────────────────────┘
/// All multi-byte integers are big-endian.
/// </summary>
internal static class BlobStreamMessage
{
    #region Reader

    /// <summary>
    /// Parse the protocol header from a full message buffer.
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

    #endregion

    #region Handshake

    /// <summary>
    /// Build a handshake response (first message after WebSocket connect).
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

    #endregion

    #region Open

    /// <summary>
    /// Parse an Open request body.
    /// Body format: [4B keyLen][UTF8 key][1B mode]
    /// tx_id is omitted from the protocol since WebSocket transactions are 1:1 bound to connections.
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

    #endregion

    #region Close

    public static long ParseHandleBody(ReadOnlySpan<byte> body)
    {
        return (long)BinaryPrimitives.ReadUInt64BigEndian(body);
    }

    #endregion

    #region Read

    public static (long Handle, int Count) ParseReadBody(ReadOnlySpan<byte> body)
    {
        var handle = (long)BinaryPrimitives.ReadUInt64BigEndian(body);
        var count = BinaryPrimitives.ReadInt32BigEndian(body.Slice(8, 4));
        return (handle, count);
    }

    #endregion

    #region Write

    public static (long Handle, byte[] Data) ParseWriteBody(ReadOnlySpan<byte> body)
    {
        var handle = (long)BinaryPrimitives.ReadUInt64BigEndian(body);
        var dataLen = BinaryPrimitives.ReadInt32BigEndian(body.Slice(8, 4));
        var data = body.Slice(12, dataLen).ToArray();
        return (handle, data);
    }

    #endregion

    #region Seek

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

    #endregion

    #region Truncate

    public static (long Handle, long NewLength) ParseTruncateBody(ReadOnlySpan<byte> body)
    {
        var handle = (long)BinaryPrimitives.ReadUInt64BigEndian(body);
        var newLength = (long)BinaryPrimitives.ReadUInt64BigEndian(body.Slice(8, 8));
        return (handle, newLength);
    }

    #endregion

    #region Writer

    /// <summary>
    /// Build a response message byte array.
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

    /// <summary>Build an Open response containing the handle ID.</summary>
    public static byte[] BuildOpenResponse(long handle)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(body, (ulong)handle);
        return BuildResponse(OpCode.Open, body);
    }

    /// <summary>Build an acknowledgement response (Close/Truncate/Commit).</summary>
    public static byte[] BuildAckResponse(uint opCode)
    {
        return BuildResponse(opCode, [0]);
    }

    /// <summary>Build a Read response containing the data.</summary>
    public static byte[] BuildReadResponse(byte[] data)
    {
        var body = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), data.Length);
        data.CopyTo(body, 4);
        return BuildResponse(OpCode.Read, body);
    }

    /// <summary>Build a Write response containing the number of bytes written.</summary>
    public static byte[] BuildWriteResponse(int bytesWritten)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(body, bytesWritten);
        return BuildResponse(OpCode.Write, body);
    }

    /// <summary>Build a Seek response containing the new position.</summary>
    public static byte[] BuildSeekResponse(long newPosition)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(body, (ulong)newPosition);
        return BuildResponse(OpCode.Seek, body);
    }

    /// <summary>Build an error response.</summary>
    public static byte[] BuildErrorResponse(BlobStreamErrorCode errorCode, string message)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var body = new byte[4 + 2 + msgBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)errorCode);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4, 2), (ushort)msgBytes.Length);
        msgBytes.CopyTo(body, 6);
        return BuildResponse(OpCode.Error, body);
    }

    /// <summary>Build an error response from a protocol exception.</summary>
    public static byte[] BuildErrorResponse(BlobStreamProtocolException ex)
        => BuildErrorResponse(ex.ErrorCode, ex.Message);

    #endregion
}
