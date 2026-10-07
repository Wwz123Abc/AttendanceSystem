using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>个人考勤查询条件。</summary>
public class PersonalAttendanceQueryDto
{
    public int      UserId     { get; set; }   // 查谁
    public DateOnly? StartDate { get; set; }   // 起始日期
    public DateOnly? EndDate   { get; set; }   // 结束日期
    public int?      Year      { get; set; }   // 或按年
    public int?      Month     { get; set; }   // 和月查询
}
