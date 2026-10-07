using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

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

    /// <summary>标准工时（取这段时间里用得最多的那个班次的标准工时，没排过班就是空）</summary>
    public decimal? StandardDailyHours { get; set; }

    public int     NightShiftDays  { get; set; }   // 夜班天数
    public decimal NightShiftHours { get; set; }   // 夜班总工时（小时，口径同 TotalWorkHours：按半小时取整累加）

    /// <summary>每天的工时（按半小时取整，跟月度合计同一个取整口径，不再是"整数舍去小数"）；
    /// 当天没有工时（休息/请假/旷工等）为 null，导出时显示空白。
    /// 下标和 TemplateReportResultDto.Dates 一一对应。</summary>
    public List<decimal?> DailyHours { get; set; } = [];

    /// <summary>每天是不是上的夜班（跨天班次，或没排班时按打卡时间兜底判断）；导出 Excel 时用来把当天格子标黄。
    /// 下标和 DailyHours/TemplateReportResultDto.Dates 一一对应。</summary>
    public List<bool> DailyIsNightShift { get; set; } = [];

    /// <summary>每天是不是"休息"（排班自己配置的每周休息日；没排班按周六周日兜底。系统已不再区分法定节假日/调班补班日）；导出 Excel 时，
    /// 休息且当天没有工时的格子标浅灰底，把"纯空白"只留给"应出勤但没有数据"，两种情况一眼能分开。
    /// 下标和 DailyHours/TemplateReportResultDto.Dates 一一对应。</summary>
    public List<bool> DailyIsRest { get; set; } = [];

    /// <summary>应出勤天数：跟网页月度汇总（MonthlySummaryDto.ExpectedWorkdays）同一个口径——
    /// 入职日期晚于周期开始的，从入职日起算；扣掉休息日（排班自己配置的每周休息日，没排班按周六周日）。</summary>
    public int ExpectedWorkdays { get; set; }

    public decimal ActualWorkdays  { get; set; }   // 出勤天数（半天假的那天算 0.5 天，跟 MonthlySummaryDto 同一口径）
    public decimal LeaveDays       { get; set; }   // 请假天数（按小时折算，半天假算 0.5 天）
    public int     RestDays        { get; set; }   // 休息天数（排班自己配置的每周休息日，没排班按周六周日）

    /// <summary>这段时间里有打卡但没有排班的天数（>0 说明是"没排班"员工：正班工时没有按班次封顶，已含工作日加班，
    /// 不能和加班总时长直接相加；导出表最后一列据此加标注）。</summary>
    public int     NoShiftDays     { get; set; }

    /// <summary>正班工时（不含加班）：跟每日打卡格子、"考勤机/打卡/审批回写"那套算法算出来的是同一个数字，
    /// 只是这里单独拆出来展示，方便区分"正常上班的时长"和"另外走加班申请批的时长"。</summary>
    public decimal RegularWorkHours { get; set; }

    public int LateMinutes         { get; set; }   // 迟到时长（分钟，合计）
    public int EarlyLeaveCount     { get; set; }   // 早退次数
    public int LateCount           { get; set; }   // 迟到次数
    public int EarlyLeaveMinutes   { get; set; }   // 早退时长（分钟，合计）
    public int MissingClockInCount { get; set; }   // 上班缺卡次数（有下班卡但没有上班卡）
    public int MissingClockOutCount{ get; set; }   // 下班缺卡次数（有上班卡但没有下班卡）
    public int AbsentDays          { get; set; }   // 旷工天数

    public decimal BusinessTripHours { get; set; }   // 出差时长

    // 注意：这四列单位是"小时"，跟 TotalWorkHours（工作时长）同一个单位口径，不是分钟——
    // 参考模板里这几列的数值大小和"工作时长"是同一量级（比如整月工作 235 小时、加班 110 小时这种），
    // 不是"迟到时长"那种几十分钟量级，之前误当成分钟数存过，导出结果会大了 60 倍，已经改正。
    public decimal TotalOvertimeHours   { get; set; }   // 加班总时长（小时）
    public decimal WeekdayOvertimeHours { get; set; }   // 工作日加班（小时）
    public decimal RestDayOvertimeHours { get; set; }   // 休息日加班（小时）
}
