namespace Adaptor.Coordinator.Models;

/// <summary>
/// 提交状态枚举
/// </summary>
public enum CommitStatus
{
    /// <summary>全部成功</summary>
    Committed = 1,
    /// <summary>部分成功（重试耗尽后）</summary>
    Partial = 2,
    /// <summary>已回滚</summary>
    RolledBack = 3,
    /// <summary>超时</summary>
    Timeout = 4,
}
