using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 原始打卡流水（对应数据库表 AttendancePunch）。
/// 每“打一次卡”就存一条：上班一条、下班一条，记录精确到分钟。
/// </summary>
[Table("AttendancePunch")]
public class AttendancePunch
{
    [Key] public int Id { get; set; }                       // 主键

    public int UserId { get; set; }                         // 哪个员工打的

    /// <summary>打卡时间（精确到分钟）</summary>
    public DateTime PunchTime { get; set; }

    /// <summary>上班 / 下班</summary>
    public PunchType PunchType { get; set; }

    public double? Latitude  { get; set; }                  // 打卡地点纬度
    public double? Longitude { get; set; }                  // 打卡地点经度

    [MaxLength(500)]
    public string? Address { get; set; }                    // 打卡地点文字地址

    [MaxLength(200)]
    public string? DeviceInfo { get; set; }                 // 打卡设备信息（钉钉同步的会标 DingTalk:xxx）

    /// <summary>是否有效（补卡审批通过后，原来的无效记录会标为 false）</summary>
    public bool IsValid { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 入库时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;                 // 对应的员工
}

/// <summary>
/// 考勤日记录（对应数据库表 AttendanceRecord）：每人每天一条，由打卡流水汇总计算得到。
/// </summary>
[Table("AttendanceRecord")]
public class AttendanceRecord
{
    [Key] public int Id { get; set; }                       // 主键

    public int UserId { get; set; }                         // 哪个员工

    /// <summary>考勤日期</summary>
    public DateOnly WorkDate { get; set; }

    /// <summary>实际上班打卡时间</summary>
    public DateTime? ClockInTime { get; set; }

    /// <summary>实际下班打卡时间</summary>
    public DateTime? ClockOutTime { get; set; }

    /// <summary>排班应上班时间</summary>
    public DateTime? ScheduledStartTime { get; set; }

    /// <summary>排班应下班时间</summary>
    public DateTime? ScheduledEndTime { get; set; }

    public AttendanceStatus AttendanceStatus { get; set; } = AttendanceStatus.Normal;  // 当天考勤状态

    /// <summary>迟到分钟数</summary>
    public int LateMinutes { get; set; } = 0;

    /// <summary>早退分钟数</summary>
    public int EarlyLeaveMinutes { get; set; } = 0;

    /// <summary>实际工时（已扣午休 / 晚餐）</summary>
    public decimal ActualWorkHours { get; set; } = 0;

    /// <summary>加班时长（小时）</summary>
    public decimal OvertimeHours { get; set; } = 0;

    /// <summary>
    /// 是否节假日。
    /// 注意：这是一个"目前没有真正被用起来"的字段——翻遍全部代码，没有任何地方会把它设成 true，
    /// 所以它的值永远是 false。节假日目前是通过 Holiday 表 + IAttendanceService.IsHolidayAsync 来判断的，
    /// 不是靠这个字段。保留它是因为删除数据库里的这一列需要做一次数据库结构变更（有部署风险），
    /// 这次重构不动数据库结构，所以先在这里写清楚，避免以后有人误以为这个字段有在用。
    /// </summary>
    public bool IsHoliday { get; set; } = false;

    /// <summary>关联审批通过后的说明（如“补卡已通过”）</summary>
    [MaxLength(200)]
    public string? ApprovalNote { get; set; }

    [MaxLength(500)]
    public string? Remark { get; set; }                     // 备注（钉钉同步的会标“钉钉同步”）

    public DateTime UpdatedAt { get; set; } = DateTime.Now; // 最后更新时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;                 // 对应的员工
}

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

    /// <summary>实际出勤天数</summary>
    public int ActualWorkdays { get; set; }

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
