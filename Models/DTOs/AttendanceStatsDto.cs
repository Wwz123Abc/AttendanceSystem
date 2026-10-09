namespace AttendanceSystem.Models.DTOs;

/// <summary>管理看板今日统计（首页看板用）。</summary>
public class AttendanceStatsDto
{
    public DateOnly StatsDate       { get; set; }   // 统计日期
    public int      TotalEmployees  { get; set; }   // 总人数
    public int      PresentCount    { get; set; }   // 出勤人数
    public int      AbsentCount     { get; set; }   // 旷工人数
    public int      LateCount       { get; set; }   // 迟到人数
    public int      OnLeaveCount    { get; set; }   // 请假人数
    public int      NotPunchedCount { get; set; }   // 未打卡人数
}
