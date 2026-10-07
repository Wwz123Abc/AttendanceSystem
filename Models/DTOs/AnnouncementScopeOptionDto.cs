namespace AttendanceSystem.Models.DTOs;

/// <summary>发布公告表单里"选部门/选考勤组"下拉框的一个选项。</summary>
public class AnnouncementScopeOptionDto
{
    public int    Id   { get; set; }
    public string Name { get; set; } = string.Empty;
}
