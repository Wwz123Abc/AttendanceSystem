namespace AttendanceSystem.Models.Enums;

/// <summary>公告的目标范围：决定这条公告要发给谁。</summary>
public enum AnnouncementScopeType
{
    All             = 1,  // 全公司
    Department      = 2,  // 指定部门（含其所有下级子部门）
    AttendanceGroup = 3,  // 指定考勤组
    DirectReports   = 4   // 发布人自己的直属下属（班组长/主管发布时锁死用这个）
}

/// <summary>AnnouncementScopeType 的辅助方法。</summary>
public static class AnnouncementScopeTypeExtensions
{
    public static string ToDisplayName(this AnnouncementScopeType type) => type switch
    {
        AnnouncementScopeType.All             => "全公司",
        AnnouncementScopeType.Department      => "指定部门",
        AnnouncementScopeType.AttendanceGroup => "指定考勤组",
        AnnouncementScopeType.DirectReports   => "我的直属下属",
        _                                     => "未知"
    };
}
