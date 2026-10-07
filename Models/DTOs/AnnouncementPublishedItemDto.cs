using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>"我发布的公告"列表用（带已读/未读统计）。</summary>
public class AnnouncementPublishedItemDto
{
    public int      Id        { get; set; }
    public string   Title     { get; set; } = string.Empty;
    public string   Content   { get; set; } = string.Empty;
    public AnnouncementScopeType ScopeType { get; set; }
    public string   ScopeText { get; set; } = string.Empty;   // "全公司"/"XX部门"/"XX考勤组"/"我的直属下属"
    public bool     IsActive  { get; set; }
    public DateTime CreatedAt { get; set; }
    public string   CreatedAtText => CreatedAt.ToString("yyyy-MM-dd HH:mm");
    public int      TotalCount { get; set; }                  // 受众总人数
    public int      ReadCount  { get; set; }                  // 已读人数
}
