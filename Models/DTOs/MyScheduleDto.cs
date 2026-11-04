using AttendanceSystem.Helpers;

namespace AttendanceSystem.Models.DTOs;

/// <summary>“我的排班”展示 DTO：员工自己某天被排的班次（方便自己看上班时间，不含打卡/工时信息）。</summary>
public class MyScheduleDto
{
    public DateOnly WorkDate { get; set; }
    public string WorkDateText  => WorkDate.ToString("yyyy-MM-dd");
    public string DayOfWeekText => WeekdayText.Zh(WorkDate);
    public bool IsWeekend => WorkDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public string  ShiftName      { get; set; } = string.Empty;
    public string  ShiftColor     { get; set; } = "#1890ff";
    public string  WorkStartText  { get; set; } = string.Empty;   // "HH:mm"
    public string  WorkEndText    { get; set; } = string.Empty;
    public bool    IsCrossDay     { get; set; }                   // 是否夜班/跨天
    public bool    IsAutoAssigned { get; set; }                   // 是否系统自动排班
}
