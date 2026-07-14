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

    /// <summary>
    /// 定位是否有效：null=未做定位校验（考勤组没开定位打卡，或没有配置地点）；
    /// true=落在考勤组某个打卡地点的有效半径内；false=离所有配置地点都太远。
    /// 目前只有"同步钉钉打卡"这条路径会填这个字段（本系统自己的打卡页超范围会直接拒绝提交，不存无效记录）。
    /// </summary>
    public bool? LocationValid { get; set; }

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

    /// <summary>
    /// 午间必打卡时间：班次配置了午间打卡窗口（ShiftSchedule.MidCheckStartTime~EndTime）时，
    /// 当天落在这个窗口内的第一次打卡（不分上/下班类型）。为空表示该窗口内没有任何打卡——
    /// 若班次确实配置了窗口，工时计算会按"上午没上班"只算下午工时（见 ComputeWorkHours 调用处）。
    /// 班次没配置窗口的，这个字段永远是空，不影响任何计算。
    /// </summary>
    public DateTime? MidCheckTime { get; set; }

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

    /// <summary>
    /// 定位异常，需要人工审核：当天从钉钉同步来的打卡里，有至少一次的定位和考勤组配置的所有地点都对不上
    /// （距离都超出有效半径）。这天的迟到/旷工等状态仍按打卡时间正常判定，只是多这一条提醒，
    /// 让管理员自己判断是不是代打卡/定位漂移之类的问题，不会自动影响出勤结果。
    /// </summary>
    public bool LocationAbnormal { get; set; } = false;

    /// <summary>定位异常的具体说明（如"打卡地点距最近的「总部大楼」850 米，超出有效范围 500 米"）</summary>
    [MaxLength(300)]
    public string? LocationAbnormalNote { get; set; }

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
