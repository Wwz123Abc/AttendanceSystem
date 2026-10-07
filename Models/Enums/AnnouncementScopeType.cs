namespace AttendanceSystem.Models.Enums;

/// <summary>公告的目标范围：决定这条公告要发给谁。</summary>
public enum AnnouncementScopeType
{
    All             = 1,  // 全公司
    Department      = 2,  // 指定部门（含其所有下级子部门）
    AttendanceGroup = 3,  // 指定考勤组
    DirectReports   = 4,  // 发布人自己的直属下属（班组长/主管发布时锁死用这个）
    Role            = 5   // 指定角色（可多选，如"管理员+文员"）；跟"全公司"一样跨部门，只有总部能发
}
