namespace AttendanceSystem.Models.DTOs;

/// <summary>管理看板"近几天出勤情况"里的一天。</summary>
public class DailyAttendanceTrendDto
{
    public DateOnly Date           { get; set; }
    public int      TotalEmployees { get; set; }   // 当前在职（需考勤）人数
    public bool     HasData        { get; set; }   // 这天有没有任何考勤记录（休息日/节假日没有）
    public int      PresentCount   { get; set; }   // 出勤
    public int      LateCount      { get; set; }   // 迟到
    public int      OnLeaveCount   { get; set; }   // 请假
    public int      AbsentCount    { get; set; }   // 旷工

    /// <summary>出勤率（百分比，一位小数）；没有数据或没有人时为 0。</summary>
    public double AttendanceRate => HasData && TotalEmployees > 0 ? Math.Round((double)PresentCount / TotalEmployees * 100, 1) : 0;
}
