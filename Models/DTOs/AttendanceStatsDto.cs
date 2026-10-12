namespace AttendanceSystem.Models.DTOs;

/// <summary>管理看板今日统计（首页看板用）。</summary>
public class AttendanceStatsDto
{
    public DateOnly StatsDate       { get; set; }   // 统计日期
    public int      TotalEmployees  { get; set; }   // 总人数
    /// <summary>应出勤人数：已办入职，扣掉请假/节假日的人，再扣掉"今天没排班或休息日、又没打卡"的人。出勤率的分母。</summary>
    public int      ExpectedCount   { get; set; }
    public int      PresentCount    { get; set; }   // 出勤人数
    public int      AbsentCount     { get; set; }   // 旷工人数
    public int      LateCount       { get; set; }   // 迟到人数
    public int      OnLeaveCount    { get; set; }   // 请假人数
    public int      NotPunchedCount { get; set; }   // 未打卡人数

    /// <summary>排了班、但还没到上班时间的人数（比如晚上才上班的夜班）：不算进应出勤，也不算未打卡。</summary>
    public int      NotStartedCount { get; set; }

    /// <summary>各考勤组今天的出勤情况。</summary>
    public List<RateBreakdownRowDto> ByGroup { get; set; } = [];

    /// <summary>各班次今天的出勤情况。</summary>
    public List<RateBreakdownRowDto> ByShift { get; set; } = [];

    /// <summary>今日出勤率（百分比，一位小数）= 已出勤 ÷ 应出勤；应出勤为 0 时是 0。</summary>
    public double AttendanceRate => ExpectedCount > 0 ? Math.Round((double)PresentCount / ExpectedCount * 100, 1) : 0;
}
