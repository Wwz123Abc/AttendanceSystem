using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

// 「DTO（数据传输对象）」= 专门用来打包一组数据、在网页/接口和程序之间传递的简单类。
// 它不直接对应数据库表，只为某个具体功能服务（比如“打卡请求”“考勤展示”）。
// 本文件放的是和考勤相关的 DTO。

// ── 打卡 ──────────────────────────────────────────────────────────────────────

/// <summary>打卡请求：员工点“打卡”时，网页传给后台的数据。</summary>
public class PunchRequestDto
{
    public PunchType PunchType  { get; set; }   // 上班还是下班
    public double?   Latitude   { get; set; }   // 当前位置纬度（定位打卡用）
    public double?   Longitude  { get; set; }   // 当前位置经度
    public string?   Address    { get; set; }   // 位置文字地址
    public string?   DeviceInfo { get; set; }   // 设备信息
}

/// <summary>打卡结果：后台处理完打卡后，返回给网页的结果。</summary>
public class PunchResponseDto
{
    public bool             Success    { get; set; }                  // 是否成功
    public string           Message    { get; set; } = string.Empty;  // 提示文字
    public DateTime?        PunchTime  { get; set; }                  // 实际打卡时间
    public AttendanceStatus Status     { get; set; }                  // 算出来的考勤状态
    public string?          StatusText { get; set; }                  // 状态中文名
    public int?             LateMinutes { get; set; }                 // 迟到分钟数（不迟到为空）
}

// ── 查询 / 展示 ───────────────────────────────────────────────────────────────

/// <summary>个人考勤查询条件。</summary>
public class PersonalAttendanceQueryDto
{
    public int      UserId     { get; set; }   // 查谁
    public DateOnly? StartDate { get; set; }   // 起始日期
    public DateOnly? EndDate   { get; set; }   // 结束日期
    public int?      Year      { get; set; }   // 或按年
    public int?      Month     { get; set; }   // 和月查询
}

/// <summary>部门/考勤组考勤查询条件。</summary>
public class DeptAttendanceQueryDto
{
    public int?      DepartmentId      { get; set; }   // 按部门
    public int?      AttendanceGroupId { get; set; }   // 或按考勤组
    public DateOnly  StartDate         { get; set; }   // 起始日期
    public DateOnly  EndDate           { get; set; }   // 结束日期
}

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

    public string ClockInText  => ClockInTime?.ToString("HH:mm")  ?? "--";   // 上班时间文字，没打卡显示 --
    public string ClockOutText => ClockOutTime?.ToString("HH:mm") ?? "--";   // 下班时间文字

    public AttendanceStatus AttendanceStatus  { get; set; }                  // 考勤状态
    public string           StatusText        { get; set; } = string.Empty;  // 状态中文名
    public string           StatusCssClass    { get; set; } = string.Empty;  // 状态对应的颜色样式

    public int     LateMinutes       { get; set; }   // 迟到分钟
    public int     EarlyLeaveMinutes { get; set; }   // 早退分钟
    public decimal ActualWorkHours   { get; set; }   // 实际工时
    public decimal OvertimeHours     { get; set; }   // 加班工时
    public bool    IsHoliday         { get; set; }   // 是否节假日（说明：目前系统里没有任何地方会把这个值设成 true，取值始终是 false，等于暂时没在用）
    public string? ApprovalNote      { get; set; }   // 审批说明

    /// <summary>定位异常，需要人工审核（钉钉同步来的打卡，定位和考勤组配置的地点对不上）。不影响上面的考勤状态判定。</summary>
    public bool    LocationAbnormal     { get; set; }
    public string? LocationAbnormalNote { get; set; }
}

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
    public int     ActualWorkdays    { get; set; }   // 实际出勤天数
    public int     NightShiftDays    { get; set; }   // 夜班天数（按排班/打卡时间实时算，不存表）
    public int     LateCount         { get; set; }   // 迟到次数
    public int     EarlyLeaveCount   { get; set; }   // 早退次数
    public int     AbsentDays        { get; set; }   // 旷工天数
    public int     NotPunchedCount   { get; set; }   // 缺卡次数
    public decimal LeaveDays         { get; set; }   // 请假天数
    public decimal TotalOvertimeHours { get; set; }  // 加班总时长
    public decimal TotalWorkHours    { get; set; }   // 实际总工时
    public int     ApprovedCount     { get; set; }   // 审批通过次数

    /// <summary>每日明细（报表 2 使用）</summary>
    public List<AttendanceRecordDto> DailyRecords { get; set; } = [];
}

/// <summary>
/// “模板月度汇总表”里一个员工的完整统计（对照公司要求的外部模板文件的列结构）。
/// 统计周期不是自然月，而是“上月26号 至 本月25号”这种薪资结算周期（由调用方传入起止日期）。
/// </summary>
public class TemplateReportRowDto
{
    public int     UserId         { get; set; }
    public string  RealName       { get; set; } = string.Empty;
    public string? GroupName      { get; set; }   // 考勤组
    public string? DeptName       { get; set; }   // 部门
    public string? EmployeeNo      { get; set; }   // 工号
    public string? Position        { get; set; }   // 职位
    public string? ContractCompany { get; set; }   // 合同公司
    public string? DingTalkUserId  { get; set; }   // 钉钉 UserId（导出报表不显示，仅内部保留）

    /// <summary>标准工时（取这段时间里用得最多的那个班次的标准工时，没排过班就是空）</summary>
    public decimal? StandardDailyHours { get; set; }

    public int NightShiftDays { get; set; }   // 夜班天数

    /// <summary>每天的工时（整数，舍去小数）；当天没有工时（休息/请假/旷工等）为 null，导出时显示空白。
    /// 下标和 TemplateReportResultDto.Dates 一一对应。</summary>
    public List<int?> DailyHours { get; set; } = [];

    /// <summary>每天是不是上的夜班（跨天班次，或没排班时按打卡时间兜底判断）；导出 Excel 时用来把当天格子标黄。
    /// 下标和 DailyHours/TemplateReportResultDto.Dates 一一对应。</summary>
    public List<bool> DailyIsNightShift { get; set; } = [];

    public int     ActualWorkdays  { get; set; }   // 出勤天数
    public int     RestDays        { get; set; }   // 休息天数（周末/法定节假日/公司休息日，不含调班补班日）
    public decimal TotalWorkHours  { get; set; }   // 工作时长（合计，保留小数，不取整——和每日格子的取整规则不同）

    public int LateMinutes         { get; set; }   // 迟到时长（分钟，合计）
    public int EarlyLeaveCount     { get; set; }   // 早退次数
    public int LateCount           { get; set; }   // 迟到次数
    public int EarlyLeaveMinutes   { get; set; }   // 早退时长（分钟，合计）
    public int MissingClockInCount { get; set; }   // 上班缺卡次数（有下班卡但没有上班卡）
    public int MissingClockOutCount{ get; set; }   // 下班缺卡次数（有上班卡但没有下班卡）
    public int AbsentDays          { get; set; }   // 旷工天数

    public decimal BusinessTripHours { get; set; }   // 出差时长
    public decimal OutHours          { get; set; }   // 外出时长（系统目前没有“外出”这个概念，恒为 0）

    // 注意：这四列单位是"小时"，跟 TotalWorkHours（工作时长）同一个单位口径，不是分钟——
    // 参考模板里这几列的数值大小和"工作时长"是同一量级（比如整月工作 235 小时、加班 110 小时这种），
    // 不是"迟到时长"那种几十分钟量级，之前误当成分钟数存过，导出结果会大了 60 倍，已经改正。
    public decimal TotalOvertimeHours   { get; set; }   // 加班总时长（小时）
    public decimal WeekdayOvertimeHours { get; set; }   // 工作日加班（小时）
    public decimal RestDayOvertimeHours { get; set; }   // 休息日加班（小时）
    public decimal HolidayOvertimeHours { get; set; }   // 节假日加班（小时）
}

/// <summary>“模板月度汇总表”整体结果：统计周期 + 每一天的日期表头 + 每个员工一行。</summary>
public class TemplateReportResultDto
{
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate   { get; set; }

    /// <summary>周期内每一天，按顺序排列（和每行的 DailyHours 下标一一对应）</summary>
    public List<DateOnly> Dates { get; set; } = [];

    public List<TemplateReportRowDto> Rows { get; set; } = [];
}

/// <summary>“我的排班”展示 DTO：员工自己某天被排的班次（方便自己看上班时间，不含打卡/工时信息）。</summary>
public class MyScheduleDto
{
    public DateOnly WorkDate { get; set; }
    public string WorkDateText  => WorkDate.ToString("yyyy-MM-dd");
    public string DayOfWeekText => WorkDate.DayOfWeek switch
    {
        DayOfWeek.Monday    => "周一", DayOfWeek.Tuesday  => "周二",
        DayOfWeek.Wednesday => "周三", DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday    => "周五", DayOfWeek.Saturday => "周六",
        _                   => "周日"
    };
    public bool IsWeekend => WorkDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public string  ShiftName      { get; set; } = string.Empty;
    public string  ShiftColor     { get; set; } = "#1890ff";
    public string  WorkStartText  { get; set; } = string.Empty;   // "HH:mm"
    public string  WorkEndText    { get; set; } = string.Empty;
    public bool    IsCrossDay     { get; set; }                   // 是否夜班/跨天
    public bool    IsAutoAssigned { get; set; }                   // 是否系统自动排班
}

/// <summary>某天的假期信息（日历页用来标注法定节假日/公司休息日/调班补班日，即使当天没有考勤记录）。</summary>
public class HolidayInfoDto
{
    public DateOnly    Date { get; set; }
    public string      Name { get; set; } = string.Empty;
    public HolidayType Type { get; set; }
}

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
    public int      LocationAbnormalCount { get; set; }   // 定位异常待审核人数
}
