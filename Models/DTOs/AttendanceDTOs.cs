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
}
