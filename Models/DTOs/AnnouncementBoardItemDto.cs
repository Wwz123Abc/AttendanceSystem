namespace AttendanceSystem.Models.DTOs;

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
