namespace Adaptor.Test.BlobStream;

/// <summary>
/// Tests for <see cref="BlobStreamMessage"/> and <see cref="OpCode"/>.
/// Design-based: derived from BLOB-STREAM-DESIGN.md §3.2–3.3.
/// </summary>
public sealed class BlobStreamMessageTest
{
    // ──────────────────────────────────────────────
    // Protocol version — BLOB-STREAM §3.2
    // ──────────────────────────────────────────────

    [Fact]
    public void ProtocolVersion_ShouldBe01()
    {
        Assert.Equal(0x01, OpCode.ProtocolVersion);
    }

    [Fact]
    public void HeaderSize_ShouldBe9Bytes()
    {
        // 1B Ver + 4B OpCode + 4B BodyLen
        Assert.Equal(9, OpCode.HeaderSize);
    }

    // ──────────────────────────────────────────────
    // OpCode definitions — BLOB-STREAM §3.3
    // ──────────────────────────────────────────────

    [Fact]
    public void OpCodes_ShouldMatchDesign()
    {
        Assert.Equal(0x00u, OpCode.Handshake);
        Assert.Equal(0x01u, OpCode.Open);
        Assert.Equal(0x02u, OpCode.Close);
        Assert.Equal(0x03u, OpCode.Read);
        Assert.Equal(0x04u, OpCode.Write);
        Assert.Equal(0x05u, OpCode.Seek);
        Assert.Equal(0x06u, OpCode.Truncate);
        Assert.Equal(0xFFu, OpCode.Error);
    }

    // ──────────────────────────────────────────────
    // ParseHeader — BLOB-STREAM §3.2
    // ──────────────────────────────────────────────

    [Fact]
    public void ParseHeader_ShouldParseValidHeader()
    {
        // Build a minimal message with version=0x01, opCode=0x03 (Read), bodyLen=12
        var msg = new byte[OpCode.HeaderSize + 12];
        msg[0] = 0x01; // version
        msg[1] = 0x00; msg[2] = 0x00; msg[3] = 0x00; msg[4] = 0x03; // opCode = 3 (big-endian)
        msg[5] = 0x00; msg[6] = 0x00; msg[7] = 0x00; msg[8] = 0x0C; // bodyLen = 12 (big-endian)

        var (opCode, bodyLen) = BlobStreamMessage.ParseHeader(msg);

        Assert.Equal(0x03u, opCode);
        Assert.Equal(12u, bodyLen);
    }

    [Fact]
    public void ParseHeader_ShouldRejectUnsupportedVersion()
    {
        var msg = new byte[OpCode.HeaderSize];
        msg[0] = 0xFF; // unsupported version

        var ex = Assert.Throws<BlobStreamProtocolException>(() =>
            BlobStreamMessage.ParseHeader(msg));

        Assert.Equal(BlobStreamErrorCode.InvalidBody, ex.ErrorCode);
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseHeader_ShouldRejectTooSmallMessage()
    {
        var msg = new byte[5]; // Less than HeaderSize (9)

        var ex = Assert.Throws<BlobStreamProtocolException>(() =>
            BlobStreamMessage.ParseHeader(msg));

        Assert.Equal(BlobStreamErrorCode.InvalidBody, ex.ErrorCode);
        Assert.Contains("too small", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // BuildResponse / ParseHeader round-trip — BLOB-STREAM §3.2
    // ──────────────────────────────────────────────

    [Fact]
    public void BuildResponse_ShouldProduceParsableHeader()
    {
        var body = new byte[] { 0x01, 0x02, 0x03 };
        var response = BlobStreamMessage.BuildResponse(OpCode.Open, body);

        var (opCode, bodyLen) = BlobStreamMessage.ParseHeader(response);
        Assert.Equal(OpCode.Open, opCode);
        Assert.Equal((uint)body.Length, bodyLen);
    }

    [Fact]
    public void BuildResponse_ShouldIncludeBodyAfterHeader()
    {
        var body = new byte[] { 0xAA, 0xBB };
        var response = BlobStreamMessage.BuildResponse(0x42, body);

        // Body starts at offset HeaderSize
        Assert.Equal(0xAA, response[OpCode.HeaderSize]);
        Assert.Equal(0xBB, response[OpCode.HeaderSize + 1]);
    }

    // ──────────────────────────────────────────────
    // Handshake — BLOB-STREAM §3.3, implicit from §4.4
    // ──────────────────────────────────────────────

    [Fact]
    public void BuildHandshakeResponse_ShouldIncludeTxIdAndExpiry()
    {
        var txId = "test-tx-12345";
        var expiresAt = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var response = BlobStreamMessage.BuildHandshakeResponse(txId, expiresAt);

        // Parse header
        var (opCode, bodyLen) = BlobStreamMessage.ParseHeader(response);
        Assert.Equal(OpCode.Handshake, opCode);

        // Body: [4B txIdLen][UTF8 txId][8B expiresAtUnixMs][1B flags]
        var body = response.AsSpan(OpCode.HeaderSize);
        var txIdLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(body);
        Assert.Equal(txId.Length, txIdLen);

        var parsedTxId = System.Text.Encoding.UTF8.GetString(body.Slice(4, txIdLen));
        Assert.Equal(txId, parsedTxId);

        var unixMs = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(body.Slice(4 + txIdLen, 8));
        var expectedUnixMs = new DateTimeOffset(expiresAt).ToUnixTimeMilliseconds();
        Assert.Equal(expectedUnixMs, unixMs);

        // Verify flags byte (default = 0 = not reconnected)
        var flags = body[4 + txIdLen + 8];
        Assert.Equal(0, flags);
    }

    [Fact]
    public void BuildHandshakeResponse_ShouldIncludeReconnectedFlag()
    {
        var txId = "test-tx-12345";
        var expiresAt = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        // Test with reconnected = true
        var response = BlobStreamMessage.BuildHandshakeResponse(txId, expiresAt, reconnected: true);

        var body = response.AsSpan(OpCode.HeaderSize);
        var txIdLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(body);
        var flags = body[4 + txIdLen + 8];
        Assert.Equal(1, flags);

        // Test with reconnected = false (explicit)
        var response2 = BlobStreamMessage.BuildHandshakeResponse(txId, expiresAt, reconnected: false);
        var body2 = response2.AsSpan(OpCode.HeaderSize);
        var flags2 = body2[4 + txIdLen + 8];
        Assert.Equal(0, flags2);
    }

    // ──────────────────────────────────────────────
    // Open — BLOB-STREAM §3.3 (table)
    // ──────────────────────────────────────────────

    [Fact]
    public void ParseOpenBody_ShouldParseKeyAndMode()
    {
        // Body: [4B keyLen][UTF8 key][1B mode]
        var key = "my-photo.jpg";
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        var body = new byte[4 + keyBytes.Length + 1];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)keyBytes.Length);
        keyBytes.CopyTo(body, 4);
        body[^1] = 0x01; // Read mode

        var (parsedKey, mode) = BlobStreamMessage.ParseOpenBody(body);

        Assert.Equal(key, parsedKey);
        Assert.Equal(0x01, mode);
    }

    [Fact]
    public void BuildOpenResponse_ShouldContainHandle()
    {
        const long handle = 0xDEADBEEFCAFE;
        var response = BlobStreamMessage.BuildOpenResponse(handle);

        var (opCode, bodyLen) = BlobStreamMessage.ParseHeader(response);
        Assert.Equal(OpCode.Open, opCode);
        Assert.Equal(8u, bodyLen); // 8B handle

        var parsedHandle = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
            response.AsSpan(OpCode.HeaderSize, 8));
        Assert.Equal(handle, parsedHandle);
    }

    // ──────────────────────────────────────────────
    // Close — BLOB-STREAM §3.3
    // ──────────────────────────────────────────────

    [Fact]
    public void ParseHandleBody_ShouldParseHandle()
    {
        var body = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body, 0x1234567890ABCDEF);

        var handle = BlobStreamMessage.ParseHandleBody(body);
        Assert.Equal(0x1234567890ABCDEF, handle);
    }

    // ──────────────────────────────────────────────
    // Read — BLOB-STREAM §3.3
    // ──────────────────────────────────────────────

    [Fact]
    public void ParseReadBody_ShouldParseHandleAndCount()
    {
        var body = new byte[8 + 4]; // 8B handle + 4B count
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), 42);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8, 4), 1024);

        var (handle, count) = BlobStreamMessage.ParseReadBody(body);

        Assert.Equal(42, handle);
        Assert.Equal(1024, count);
    }

    [Fact]
    public void BuildReadResponse_ShouldIncludeData()
    {
        var data = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var response = BlobStreamMessage.BuildReadResponse(data);

        var (opCode, bodyLen) = BlobStreamMessage.ParseHeader(response);
        Assert.Equal(OpCode.Read, opCode);
        Assert.Equal(4u + (uint)data.Length, bodyLen); // 4B dataLen + data

        var body = response.AsSpan(OpCode.HeaderSize);
        var dataLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(body);
        Assert.Equal(data.Length, dataLen);

        var parsedData = body.Slice(4, dataLen).ToArray();
        Assert.Equal(data, parsedData);
    }

    // ──────────────────────────────────────────────
    // Write — BLOB-STREAM §3.3
    // ──────────────────────────────────────────────

    [Fact]
    public void ParseWriteBody_ShouldParseHandleAndData()
    {
        var data = new byte[] { 0xAA, 0xBB, 0xCC };
        var body = new byte[8 + 4 + data.Length]; // 8B handle + 4B dataLen + data
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), 7);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8, 4), data.Length);
        data.CopyTo(body, 12);

        var (handle, parsedData) = BlobStreamMessage.ParseWriteBody(body);

        Assert.Equal(7, handle);
        Assert.Equal(data, parsedData);
    }

    [Fact]
    public void BuildWriteResponse_ShouldContainBytesWritten()
    {
        var response = BlobStreamMessage.BuildWriteResponse(65536);
        var (opCode, bodyLen) = BlobStreamMessage.ParseHeader(response);
        Assert.Equal(OpCode.Write, opCode);
        Assert.Equal(4u, bodyLen);

        var bytesWritten = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
            response.AsSpan(OpCode.HeaderSize, 4));
        Assert.Equal(65536, bytesWritten);
    }

    // ──────────────────────────────────────────────
    // Seek — BLOB-STREAM §3.3
    // ──────────────────────────────────────────────

    [Fact]
    public void ParseSeekBody_ShouldParseHandleOffsetAndOrigin()
    {
        var body = new byte[8 + 8 + 1]; // 8B handle + 8B offset + 1B whence
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), 100);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(8, 8), 5000);
        body[16] = 1; // SeekOrigin.Current

        var (handle, offset, origin) = BlobStreamMessage.ParseSeekBody(body);

        Assert.Equal(100, handle);
        Assert.Equal(5000, offset);
        Assert.Equal(SeekOrigin.Current, origin);
    }

    [Fact]
    public void ParseSeekBody_ShouldRejectInvalidOrigin()
    {
        var body = new byte[8 + 8 + 1];
        body[16] = 0xFF; // Invalid whence

        var ex = Assert.Throws<BlobStreamProtocolException>(() =>
            BlobStreamMessage.ParseSeekBody(body));

        Assert.Equal(BlobStreamErrorCode.InvalidBody, ex.ErrorCode);
    }

    [Fact]
    public void ParseSeekBody_ShouldMapOriginsCorrectly()
    {
        // whence 0 = Begin, 1 = Current, 2 = End
        TestSeekOrigin(0, SeekOrigin.Begin);
        TestSeekOrigin(1, SeekOrigin.Current);
        TestSeekOrigin(2, SeekOrigin.End);

        void TestSeekOrigin(byte whence, SeekOrigin expected)
        {
            var body = new byte[8 + 8 + 1];
            body[16] = whence;
            var (_, _, origin) = BlobStreamMessage.ParseSeekBody(body);
            Assert.Equal(expected, origin);
        }
    }

    [Fact]
    public void BuildSeekResponse_ShouldContainNewPosition()
    {
        var response = BlobStreamMessage.BuildSeekResponse(12345);
        var (opCode, bodyLen) = BlobStreamMessage.ParseHeader(response);
        Assert.Equal(OpCode.Seek, opCode);
        Assert.Equal(8u, bodyLen);

        var newPos = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
            response.AsSpan(OpCode.HeaderSize, 8));
        Assert.Equal(12345, newPos);
    }

    // ──────────────────────────────────────────────
    // Truncate — BLOB-STREAM §3.3
    // ──────────────────────────────────────────────

    [Fact]
    public void ParseTruncateBody_ShouldParseHandleAndNewLength()
    {
        var body = new byte[8 + 8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), 50);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(8, 8), 1024);

        var (handle, newLength) = BlobStreamMessage.ParseTruncateBody(body);

        Assert.Equal(50, handle);
        Assert.Equal(1024, newLength);
    }

    [Fact]
    public void BuildAckResponse_ShouldContainStatusByte()
    {
        var response = BlobStreamMessage.BuildAckResponse(OpCode.Close);
        var (opCode, bodyLen) = BlobStreamMessage.ParseHeader(response);
        Assert.Equal(OpCode.Close, opCode);
        Assert.Equal(1u, bodyLen);
        Assert.Equal(0, response[OpCode.HeaderSize]); // status = 0 (OK)
    }

    // ──────────────────────────────────────────────
    // Error — BLOB-STREAM §3.3, §3.5
    // ──────────────────────────────────────────────

    [Fact]
    public void BuildErrorResponse_ShouldContainErrorCodeAndMessage()
    {
        var ex = new BlobStreamProtocolException(BlobStreamErrorCode.KeyNotFound, "key not found");
        var response = BlobStreamMessage.BuildErrorResponse(ex);

        var (opCode, _) = BlobStreamMessage.ParseHeader(response);
        Assert.Equal(OpCode.Error, opCode);

        // Body: [4B errorCode][2B msgLen][UTF8 msg]
        var body = response.AsSpan(OpCode.HeaderSize);
        var errorCode = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body);
        Assert.Equal((uint)BlobStreamErrorCode.KeyNotFound, errorCode);

        var msgLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(body.Slice(4, 2));
        var msg = System.Text.Encoding.UTF8.GetString(body.Slice(6, msgLen));
        Assert.Equal("key not found", msg);
    }

    // ──────────────────────────────────────────────
    // Error code values — BLOB-STREAM §3.5
    // ──────────────────────────────────────────────

    [Fact]
    public void ErrorCodes_ShouldMatchDesign()
    {
        Assert.Equal(0x0001u, (uint)BlobStreamErrorCode.InvalidOpCode);
        Assert.Equal(0x0002u, (uint)BlobStreamErrorCode.InvalidBody);
        Assert.Equal(0x0003u, (uint)BlobStreamErrorCode.InvalidHandle);
        Assert.Equal(0x0004u, (uint)BlobStreamErrorCode.TransactionNotFound);
        Assert.Equal(0x0005u, (uint)BlobStreamErrorCode.TransactionNotActive);
        Assert.Equal(0x0006u, (uint)BlobStreamErrorCode.KeyNotFound);
        Assert.Equal(0x0007u, (uint)BlobStreamErrorCode.KeyAlreadyExists);
        Assert.Equal(0x0008u, (uint)BlobStreamErrorCode.DriverNotAvailable);
        Assert.Equal(0x0009u, (uint)BlobStreamErrorCode.IoError);
        Assert.Equal(0x000Au, (uint)BlobStreamErrorCode.HandleLimitExceeded);
        Assert.Equal(0x000Bu, (uint)BlobStreamErrorCode.ReadBeyondEnd);
        Assert.Equal(0xFFFFu, (uint)BlobStreamErrorCode.UnknownError);
    }

    // ──────────────────────────────────────────────
    // Open mode values — BLOB-STREAM §3.4
    // ──────────────────────────────────────────────

    [Fact]
    public void OpenMode_ShouldAlignWithBlobAccessMode()
    {
        // The protocol mode byte should align with BlobAccessMode enum values
        Assert.Equal(0x01, (int)BlobAccessMode.Read);
        Assert.Equal(0x02, (int)BlobAccessMode.Write);
        Assert.Equal(0x03, (int)BlobAccessMode.ReadWrite);
        Assert.Equal(0x04, (int)BlobAccessMode.Create);
        Assert.Equal(0x05, (int)BlobAccessMode.CreateOrReplace);
        Assert.Equal(0x06, (int)BlobAccessMode.Append);
    }

    // ──────────────────────────────────────────────
    // Big-endian contract — BLOB-STREAM §3.2
    // ──────────────────────────────────────────────

    [Fact]
    public void AllMultiByteIntegers_ShouldBeBigEndian()
    {
        // Verify with a known value: 0x01020304 in big-endian
        var value = 0x01020304u;
        var body = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(body, value);

        Assert.Equal(0x01, body[0]);
        Assert.Equal(0x02, body[1]);
        Assert.Equal(0x03, body[2]);
        Assert.Equal(0x04, body[3]);
    }

    // ──────────────────────────────────────────────
    // Round-trip: Parse after Build — protocol consistency
    // ──────────────────────────────────────────────

    [Fact]
    public void ReadBody_RoundTrip()
    {
        const long handle = 123;
        const int count = 4096;

        // Build a fake read body manually since there's no BuildReadBody method
        var body = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), (ulong)handle);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8, 4), count);

        var (parsedHandle, parsedCount) = BlobStreamMessage.ParseReadBody(body);

        Assert.Equal(handle, parsedHandle);
        Assert.Equal(count, parsedCount);
    }

    [Fact]
    public void SeekBody_RoundTrip()
    {
        const long handle = 99;
        const long offset = 2048;
        const byte whence = 2; // SeekOrigin.End

        var body = new byte[17];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), (ulong)handle);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(8, 8), (ulong)offset);
        body[16] = whence;

        var (parsedHandle, parsedOffset, parsedOrigin) = BlobStreamMessage.ParseSeekBody(body);

        Assert.Equal(handle, parsedHandle);
        Assert.Equal(offset, parsedOffset);
        Assert.Equal(SeekOrigin.End, parsedOrigin);
    }
}
