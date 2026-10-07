namespace AttendanceSystem.Models.Enums;

/// <summary>ApprovalLevelType 的辅助方法。</summary>
public static class ApprovalLevelTypeExtensions
{
    /// <summary>把审批层级转成中文名，供页面显示用。</summary>
    public static string ToDisplayName(this ApprovalLevelType level) => level switch
    {
        ApprovalLevelType.Level1 => "一级审批（班组长）",
        ApprovalLevelType.Level2 => "二级审批（班组长 + 直属上级）",
        _                        => "一级审批（班组长）"
    };
}
