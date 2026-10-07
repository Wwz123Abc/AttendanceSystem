namespace AttendanceSystem.Models.Enums;

/// <summary>AnnouncementScopeType 的辅助方法。</summary>
public static class AnnouncementScopeTypeExtensions
{
    public static string ToDisplayName(this AnnouncementScopeType type) => type switch
    {
        AnnouncementScopeType.All             => "全公司",
        AnnouncementScopeType.Department      => "指定部门",
        AnnouncementScopeType.AttendanceGroup => "指定考勤组",
        AnnouncementScopeType.DirectReports   => "我的直属下属",
        AnnouncementScopeType.Role            => "指定角色",
        _                                     => "未知"
    };
}
