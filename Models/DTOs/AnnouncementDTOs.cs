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
}

/// <summary>公告栏（员工侧）展示一条公告。</summary>
public class AnnouncementBoardItemDto
{
    public int      Id            { get; set; }
    public string   Title         { get; set; } = string.Empty;
    public string   Content       { get; set; } = string.Empty;
    public string   PublisherName { get; set; } = string.Empty;
    public DateTime CreatedAt     { get; set; }
    public string   CreatedAtText => CreatedAt.ToString("yyyy-MM-dd HH:mm");
    public bool     IsRead        { get; set; }
}

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

/// <summary>某条公告的已读明细（谁读了、谁没读），未读的排在前面方便一眼看出还差谁。</summary>
public class AnnouncementReadDetailDto
{
    public int       UserId     { get; set; }
    public string    RealName   { get; set; } = string.Empty;
    public string    EmployeeNo { get; set; } = string.Empty;
    public DateTime? ReadAt     { get; set; }
    public string    ReadAtText => ReadAt.HasValue ? ReadAt.Value.ToString("yyyy-MM-dd HH:mm") : "未读";
}

/// <summary>发布公告表单里"选部门/选考勤组"下拉框的一个选项。</summary>
public class AnnouncementScopeOptionDto
{
    public int    Id   { get; set; }
    public string Name { get; set; } = string.Empty;
}
