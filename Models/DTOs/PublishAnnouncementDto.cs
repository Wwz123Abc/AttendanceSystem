using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

// 本文件放的是和系统公告相关的 DTO。

/// <summary>发布公告时，页面传给后台的数据。</summary>
public class PublishAnnouncementDto
{
    public string Title   { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    /// <summary>发给谁；班组长/主管发布时这个值不生效，后台会强制改成"我的直属下属"。</summary>
    public AnnouncementScopeType ScopeType { get; set; }

    /// <summary>配合 ScopeType=Department/AttendanceGroup 用，填对应的部门/考勤组 Id。</summary>
    public int? ScopeId { get; set; }

    /// <summary>配合 ScopeType=Role 用，选中的角色（可多选）。</summary>
    public List<UserRole>? ScopeRoles { get; set; }
}
