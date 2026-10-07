using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>月度考勤汇总展示 DTO（用于报表 1 总表 + 报表 2 个人明细）。</summary>
public class MonthlySummaryDto
{
    public int     UserId    { get; set; }
    public string  EmployeeNo { get; set; } = string.Empty;
    public string  RealName   { get; set; } = string.Empty;
    public string? DeptName   { get; set; }
    public string? Position   { get; set; }

    public int Year  { get; set; }
    public int Month { get; set; }

    public int     ExpectedWorkdays  { get; set; }   // 应出勤天数
    public decimal ActualWorkdays    { get; set; }   // 实际出勤天数（半天假的那天算 0.5 天）
    public int     NightShiftDays    { get; set; }   // 夜班天数（按排班/打卡时间实时算，不存表）
    public int     LateCount         { get; set; }   // 迟到次数
    public int     EarlyLeaveCount   { get; set; }   // 早退次数
    public int     AbsentDays        { get; set; }   // 旷工天数
    public int     NotPunchedCount   { get; set; }   // 缺卡次数
    public decimal LeaveDays         { get; set; }   // 请假天数
    public decimal TotalOvertimeHours { get; set; }  // 加班总时长
    public decimal TotalWorkHours    { get; set; }   // 实际总工时
    public int     ApprovedCount     { get; set; }   // 审批通过次数
    /// <summary>这个月有打卡但没有排班的天数：没排班的人正班工时不按班次封顶，已经包含了工作日加班时间，
    /// 不能直接和加班总时长相加；导出表里用它给这类员工加"没排班"标注。</summary>
    public int     NoShiftDays       { get; set; }

    /// <summary>每日明细（报表 2 使用）</summary>
    public List<AttendanceRecordDto> DailyRecords { get; set; } = [];
}
