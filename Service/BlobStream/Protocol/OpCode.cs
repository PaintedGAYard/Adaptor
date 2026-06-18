namespace Adaptor.Service.BlobStream.Protocol;

/// <summary>
/// BlobStream WebSocket 协议操作码
/// </summary>
internal static class OpCode
{
    /// <summary>服务端→客户端: 握手（连接建立后首条消息，携带 tx_id）</summary>
    public const uint Handshake = 0x00;
    /// <summary>请求: 打开 Blob</summary>
    public const uint Open = 0x01;
    /// <summary>请求: 关闭 Blob</summary>
    public const uint Close = 0x02;
    /// <summary>请求: 读取数据</summary>
    public const uint Read = 0x03;
    /// <summary>请求: 写入数据</summary>
    public const uint Write = 0x04;
    /// <summary>请求: 移动读写位置</summary>
    public const uint Seek = 0x05;
    /// <summary>请求: 截断 Blob</summary>
    public const uint Truncate = 0x06;
    /// <summary>请求: 提交事务（一次性最终语义，不可重复调用）</summary>
    public const uint Commit = 0x07;

    /// <summary>响应: 错误</summary>
    public const uint Error = 0xFF;

    /// <summary>当前协议版本</summary>
    public const byte ProtocolVersion = 0x01;

    /// <summary>固定头部大小 (Ver + OpCode + BodyLen)</summary>
    public const int HeaderSize = 1 + 4 + 4; // 9 bytes
}
