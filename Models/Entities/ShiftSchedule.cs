using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 班次定义（对应数据库表 ShiftSchedule）。
/// 描述“一个班怎么上”：几点上下班、迟到/早退能宽限几分钟、超多久算加班等。
/// </summary>
[Table("ShiftSchedule")]
public class ShiftSchedule
{
    [Key] public int Id { get; set; }                       // 主键：班次唯一编号

    public int AttendanceGroupId { get; set; }              // 属于哪个考勤组

    /// <summary>班次名称，如：正常班 / 早班 / 夜班</summary>
    [Required, MaxLength(50)]
    public string ShiftName { get; set; } = string.Empty;

    public ShiftType ShiftType { get; set; } = ShiftType.Fixed;  // 班次类型（固定/弹性/自由）

    /// <summary>上班时间</summary>
    public TimeOnly WorkStartTime { get; set; }

    /// <summary>下班时间</summary>
    public TimeOnly WorkEndTime { get; set; }

    /// <summary>迟到容忍分钟数：晚这么多分钟内不算迟到</summary>
    public int LateToleranceMinutes { get; set; } = 5;

    /// <summary>早退容忍分钟数：早这么多分钟内不算早退</summary>
    public int EarlyLeaveToleranceMinutes { get; set; } = 5;

    /// <summary>最多可提前多少分钟打上班卡</summary>
    public int EarliestClockInMinutes { get; set; } = 60;

    /// <summary>超过下班时间多少分钟才算加班</summary>
    public int OvertimeThresholdMinutes { get; set; } = 30;

    /// <summary>是否跨天班次（如夜班，下班时间落到第二天）</summary>
    public bool IsCrossDay { get; set; } = false;

    /// <summary>标准工时（小时）</summary>
    public decimal StandardWorkHours { get; set; } = 8;

    /// <summary>日历上显示的颜色</summary>
    [MaxLength(20)]
    public string Color { get; set; } = "#1890ff";

    public bool IsActive { get; set; } = true;              // 是否启用

    /// <summary>
    /// 每周休息日：哪几天不用上班，存成逗号隔开的星期几编号（0=周日,1=周一...6=周六，和 C# DayOfWeek 编号一致）。
    /// 默认"0,6"＝周六周日休息；三班倒这类班次可以改成别的组合（比如休二、三）。
    /// </summary>
    [MaxLength(20)]
    public string RestDaysOfWeek { get; set; } = "0,6";

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 创建时间
    public DateTime UpdatedAt { get; set; } = DateTime.Now; // 最后修改时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("AttendanceGroupId")]
    public AttendanceGroup AttendanceGroup { get; set; } = null!;        // 所属考勤组

    public ICollection<ShiftAssignment> ShiftAssignments { get; set; } = [];  // 用了这个班次的排班记录
}

/// <summary>ShiftSchedule 的辅助方法：解析/判断"每周休息日"。</summary>
public static class ShiftScheduleExtensions
{
    /// <summary>把 RestDaysOfWeek 这个逗号分隔字符串解析成星期几的集合。</summary>
    public static HashSet<DayOfWeek> ParseRestDays(this ShiftSchedule shift) =>
        (shift.RestDaysOfWeek ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var n) && n is >= 0 and <= 6 ? (DayOfWeek?)n : null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .ToHashSet();

    /// <summary>某个星期几是不是这个班次配置的"每周休息日"。</summary>
    public static bool IsRestDay(this ShiftSchedule shift, DayOfWeek day) => shift.ParseRestDays().Contains(day);
}

/// <summary>
/// 员工排班记录（对应数据库表 ShiftAssignment）：某员工某一天用哪个班次。
/// </summary>
[Table("ShiftAssignment")]
public class ShiftAssignment
{
    [Key] public int Id { get; set; }                       // 主键

    public int UserId          { get; set; }                // 哪个员工
    public int ShiftScheduleId { get; set; }                // 用哪个班次

    /// <summary>排班日期</summary>
    public DateOnly WorkDate { get; set; }

    /// <summary>是否由系统自动排班（false=人工排的）</summary>
    public bool IsAutoAssigned { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 创建时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;                 // 对应的员工

    [ForeignKey("ShiftScheduleId")]
    public ShiftSchedule ShiftSchedule { get; set; } = null!;  // 对应的班次
}
