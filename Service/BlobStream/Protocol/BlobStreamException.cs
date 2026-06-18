namespace Adaptor.Service.BlobStream.Protocol;

/// <summary>
/// BlobStream 协议错误码
/// </summary>
internal enum BlobStreamErrorCode : uint
{
    InvalidOpCode = 0x0001,
    InvalidBody = 0x0002,
    InvalidHandle = 0x0003,
    TransactionNotFound = 0x0004,
    TransactionNotActive = 0x0005,
    KeyNotFound = 0x0006,
    KeyAlreadyExists = 0x0007,
    DriverNotAvailable = 0x0008,
    IoError = 0x0009,
    HandleLimitExceeded = 0x000A,
    ReadBeyondEnd = 0x000B,
    UnknownError = 0xFFFF,
}

/// <summary>
/// BlobStream 协议异常
/// </summary>
internal sealed class BlobStreamProtocolException : Exception
{
    public BlobStreamErrorCode ErrorCode { get; }

    public BlobStreamProtocolException(BlobStreamErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public BlobStreamProtocolException(BlobStreamErrorCode errorCode, string message, Exception inner)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}
