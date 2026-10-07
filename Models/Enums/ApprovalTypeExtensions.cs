namespace AttendanceSystem.Models.Enums;

// 扩展方法：给 ApprovalType 增加一个把英文翻译成中文的小工具，供页面/通知文案使用。
/// <summary>ApprovalType 的辅助方法。</summary>
public static class ApprovalTypeExtensions
{
    /// <summary>把审批类型（如 Leave）转成中文名（如「请假」）。</summary>
    public static string ToDisplayName(this ApprovalType type) => type switch
    {
        ApprovalType.PunchReplenishment => "补卡",
        ApprovalType.Leave              => "请假",
        ApprovalType.Overtime           => "加班",
        ApprovalType.BusinessTrip       => "出差",
        _                               => "其他"   // 兜底，正常不会走到这里
    };
}
