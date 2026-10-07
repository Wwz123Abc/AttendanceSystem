using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 月度考勤汇总（对应数据库表 MonthlyAttendanceSummary）：每人每月一条，月末自动统计生成。
/// </summary>
[Table("MonthlyAttendanceSummary")]
public class MonthlyAttendanceSummary
{
    [Key] public int Id { get; set; }                       // 主键

    public int UserId { get; set; }                         // 哪个员工

    /// <summary>年份</summary>
    public int Year { get; set; }

    /// <summary>月份（1–12）</summary>
    public int Month { get; set; }

    /// <summary>应出勤天数（扣除节假日）</summary>
    public int ExpectedWorkdays { get; set; }

    /// <summary>实际出勤天数：半天请假的那天只算 0.5 天出勤，不再跟请假天数重复记满整天（2026-09-18 起）。</summary>
    public decimal ActualWorkdays { get; set; }

    public int     LateCount        { get; set; } = 0;      // 迟到次数
    public int     EarlyLeaveCount  { get; set; } = 0;      // 早退次数
    public int     AbsentDays       { get; set; } = 0;      // 旷工天数
    public int     NotPunchedCount  { get; set; } = 0;      // 缺卡次数
    public decimal LeaveDays        { get; set; } = 0;      // 请假天数

    /// <summary>月度加班总时长（小时）</summary>
    public decimal TotalOvertimeHours { get; set; } = 0;

    /// <summary>月度实际总工时（小时）</summary>
    public decimal TotalWorkHours { get; set; } = 0;

    /// <summary>审批通过次数</summary>
    public int ApprovedCount { get; set; } = 0;

    public DateTime GeneratedAt { get; set; } = DateTime.Now;  // 生成时间
    public DateTime UpdatedAt   { get; set; } = DateTime.Now;  // 最后更新时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;                 // 对应的员工
}
