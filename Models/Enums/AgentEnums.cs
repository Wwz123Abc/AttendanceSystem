namespace AttendanceSystem.Models.Enums;

/// <summary>AGENT 待确认动作（提案）的状态。</summary>
public enum AgentActionStatus
{
    Pending  = 1,   // 提案已生成，等管理员确认
    Approved = 2,   // 管理员确认通过，已执行（或执行失败，见 ErrorText）
    Rejected = 3,   // 管理员拒绝执行
    Expired  = 4    // 超时未处理自动失效
}
