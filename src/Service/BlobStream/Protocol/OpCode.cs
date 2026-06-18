namespace Adaptor.Service.BlobStream.Protocol;

/// <summary>
/// BlobStream WebSocket protocol opcodes.
/// </summary>
internal static class OpCode
{
    /// <summary>Server→Client: handshake (first message after connect, carries tx_id)</summary>
    public const uint Handshake = 0x00;
    /// <summary>Request: open a BLOB</summary>
    public const uint Open = 0x01;
    /// <summary>Request: close a BLOB</summary>
    public const uint Close = 0x02;
    /// <summary>Request: read data</summary>
    public const uint Read = 0x03;
    /// <summary>Request: write data</summary>
    public const uint Write = 0x04;
    /// <summary>Request: seek to a position</summary>
    public const uint Seek = 0x05;
    /// <summary>Request: truncate a BLOB</summary>
    public const uint Truncate = 0x06;
    /// <summary>Request: commit the transaction (final semantics, cannot be retried)</summary>
    public const uint Commit = 0x07;

    /// <summary>Response: error</summary>
    public const uint Error = 0xFF;

    /// <summary>Current protocol version.</summary>
    public const byte ProtocolVersion = 0x01;

    /// <summary>Fixed header size (Ver + OpCode + BodyLen).</summary>
    public const int HeaderSize = 1 + 4 + 4; // 9 bytes
}
