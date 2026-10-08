using Microsoft.AspNetCore.Authorization;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Attendance;

/// <summary>
/// 我的考勤日历：按薪资周期（上月 26 日 ~ 本月 25 日，见 <see cref="PayrollCycle"/>）显示一个日历，
/// 每天用颜色标出考勤状态（绿=正常、红=异常、蓝=休假类），点某一天可以看那天的打卡明细和排班信息。
/// 日历上方的汇总数字（含总工时）取自"发薪考勤汇总表"同一套算法，员工看到的总工时和发薪汇总表一致。
/// </summary>
[Authorize]
public class MyCalendarModel(IAttendanceService attendanceService) : AppPageModel
{
    /// <summary>周期的结束年月（10 月周期 = 9/26~10/25，Year=2026、Month=10）。</summary>
    public int Year  { get; set; }
    public int Month { get; set; }
    public DateOnly CycleStart { get; set; }
    public DateOnly CycleEnd   { get; set; }

    /// <summary>本周期汇总（出勤/迟到/旷工/请假/发薪总工时），显示在日历上方；免考勤账号没有，页面按 0 显示。</summary>
    public TemplateReportRowDto? Payroll { get; set; }

    /// <summary>日历网格里的一天：null 表示周期开头用来对齐星期几的占位空格。</summary>
    public record CalendarCell(DateOnly Date, AttendanceRecordDto? Record, MyScheduleDto? Shift);

    /// <summary>日历网格，已按“周一开头”补好第一周前面的占位空格，按 7 个一组渲染成一行。</summary>
    public List<CalendarCell?> Cells { get; set; } = [];

    public async Task OnGetAsync(int? year, int? month, CancellationToken ct)
    {
        // year/month 是周期的结束月份，来自网址：非法值、超出"最近 24 个月到下个周期"的一律退回当前周期，
        // 不然任何登录员工改网址一路遍历，每个月份都会触发一次整周期的计算
        (Year, Month) = PayrollCycle.ResolveRequested(year, month, DateOnly.FromDateTime(DateTime.Today));
        (CycleStart, CycleEnd) = PayrollCycle.Range(Year, Month);
        var userId = CurrentUserId;

        var records = await attendanceService.GetPersonalAttendanceAsync(new PersonalAttendanceQueryDto
        {
            UserId = userId, StartDate = CycleStart, EndDate = CycleEnd
        });
        var schedule = await attendanceService.GetMyScheduleAsync(userId, CycleStart, CycleEnd);
        Payroll = await attendanceService.GetMyPayrollRowAsync(userId, CycleStart, CycleEnd, ct);

        var recByDate   = records.ToDictionary(r => r.WorkDate);
        var shiftByDate = schedule.ToDictionary(s => s.WorkDate);

        // 把“周日=0”的 DayOfWeek 转成“周一开头”的偏移量，补齐第一周前面的空格，让周期第一天对齐到正确的星期几
        var leadingBlanks = ((int)CycleStart.DayOfWeek + 6) % 7;

        Cells = [];
        for (var i = 0; i < leadingBlanks; i++) Cells.Add(null);
        for (var d = CycleStart; d <= CycleEnd; d = d.AddDays(1))
            Cells.Add(new CalendarCell(d, recByDate.GetValueOrDefault(d), shiftByDate.GetValueOrDefault(d)));
    }
}
