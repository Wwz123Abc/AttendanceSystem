using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>考勤日记录展示 DTO（用于页面表格显示一天的考勤）。</summary>
public class AttendanceRecordDto
{
    public int     Id     { get; set; }
    public int     UserId { get; set; }

    /// <summary>员工信息（部门统计时才填充）</summary>
    public string? EmployeeNo { get; set; }
    public string? RealName   { get; set; }
    public string? DeptName   { get; set; }

    public DateOnly WorkDate { get; set; }

    // 下面这些 => 是“计算属性”：自动根据上面的值算出来，给页面直接显示
    public string WorkDateText  => WorkDate.ToString("yyyy-MM-dd");   // 日期文字
    public string DayOfWeekText => WorkDate.DayOfWeek switch          // 星期几（中文）
    {
        DayOfWeek.Monday    => "周一", DayOfWeek.Tuesday  => "周二",
        DayOfWeek.Wednesday => "周三", DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday    => "周五", DayOfWeek.Saturday => "周六",
        _                   => "周日"
    };

    public DateTime? ClockInTime  { get; set; }
    public DateTime? ClockOutTime { get; set; }

    /// <summary>午间必打卡结果：每一段窗口各一条；班次没配置午间窗口的，这个列表是空的。</summary>
    public List<MidCheckWindowResult> MidCheckHits { get; set; } = [];

    public string ClockInText  => ClockInTime?.ToString("HH:mm")  ?? "--";   // 上班时间文字，没打卡显示 --
    public string ClockOutText => ClockOutTime?.ToString("HH:mm") ?? "--";   // 下班时间文字

    /// <summary>每一段午间打卡的展示文字，如 "10:00-10:10: 10:05"；没打上显示 "--"。</summary>
    public List<string> MidCheckTexts => MidCheckHits
        .Select(h => $"{h.WindowStart:HH\\:mm}-{h.WindowEnd:HH\\:mm}: {(h.HitTime.HasValue ? h.HitTime.Value.ToString("HH\\:mm") : "--")}")
        .ToList();

    /// <summary>午间打卡时间，简洁版（只显示打卡时刻，多段用"、"隔开），给报表/列表这种寸土寸金的地方用；没配窗口或都没打上显示 "--"。</summary>
    public string MidCheckTimeText => MidCheckHits.Count == 0
        ? "--"
        : string.Join("、", MidCheckHits.Select(h => h.HitTime.HasValue ? h.HitTime.Value.ToString("HH:mm") : "--"));

    public AttendanceStatus AttendanceStatus  { get; set; }                  // 考勤状态
    public string           StatusText        { get; set; } = string.Empty;  // 状态中文名
    public string           StatusCssClass    { get; set; } = string.Empty;  // 状态对应的颜色样式

    public int     LateMinutes       { get; set; }   // 迟到分钟
    public int     EarlyLeaveMinutes { get; set; }   // 早退分钟
    public decimal ActualWorkHours   { get; set; }   // 实际工时
    public decimal OvertimeHours     { get; set; }   // 加班工时
    public decimal LeaveHours        { get; set; }   // 请假工时
    public bool    IsHoliday         { get; set; }   // 是否节假日（说明：目前系统里没有任何地方会把这个值设成 true，取值始终是 false，等于暂时没在用）
    public string? ApprovalNote      { get; set; }   // 审批说明

    /// <summary>定位异常，需要人工审核（打卡定位和考勤组配置的地点对不上）。不影响上面的考勤状态判定。</summary>
    public bool    LocationAbnormal     { get; set; }
    public string? LocationAbnormalNote { get; set; }
}
