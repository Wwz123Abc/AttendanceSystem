namespace AttendanceSystem.Models.DTOs;

/// <summary>某条公告的已读明细（谁读了、谁没读），未读的排在前面方便一眼看出还差谁。</summary>
public class AnnouncementReadDetailDto
{
    public int       UserId     { get; set; }
    public string    RealName   { get; set; } = string.Empty;
    public string    EmployeeNo { get; set; } = string.Empty;
    public DateTime? ReadAt     { get; set; }
    public string    ReadAtText => ReadAt.HasValue ? ReadAt.Value.ToString("yyyy-MM-dd HH:mm") : "未读";
}
