using Microsoft.AspNetCore.Authorization;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Attendance;

/// <summary>
/// 我的考勤日历：按月份显示一个日历，每天用颜色标出考勤状态（绿=正常、红=异常、蓝=休假类），
/// 点某一天可以看那天的打卡明细和排班信息。取代原来纯列表式的“我的排班”。
/// </summary>
[Authorize]
public class MyCalendarModel(IAttendanceService attendanceService) : AppPageModel
{
    public int Year  { get; set; }
    public int Month { get; set; }

    /// <summary>本月汇总统计（出勤/迟到/缺勤/请假/加班等），显示在日历上方，一眼看整月概况。</summary>
    public MonthlySummaryDto? Summary { get; set; }

    /// <summary>日历网格里的一天：null 表示当月开头/结尾用来对齐星期几的占位空格。</summary>
    public record CalendarCell(DateOnly Date, AttendanceRecordDto? Record, MyScheduleDto? Shift, HolidayInfoDto? Holiday);

    /// <summary>日历网格，已按“周一开头”补好每周前面的占位空格，按 7 个一组渲染成一行。</summary>
    public List<CalendarCell?> Cells { get; set; } = [];

    public async Task OnGetAsync(int? year, int? month)
    {
        // year/month 直接来自 URL，非法值（如 month=13）构造 DateOnly 时会直接抛异常报 500，
        // 这里跟"没传"一样兜底成当前年月，而不是校验失败就 500
        Year  = year  is >= 2000 and <= 2100 ? year.Value : DateTime.Today.Year;
        Month = month is >= 1 and <= 12 ? month.Value : DateTime.Today.Month;
        // 年月合法（比如 2000~2100 内）不代表可以随便查——任何登录员工改网址上的年月一路遍历，
        // 每个没生成过汇总的月份都会触发一次 EnsureMonthlySummaryFreshAsync 重算+写库。
        // 这里只放开"最近 24 个月以内（含下个月）"，超出范围一律退回当前年月，跟上面越界兜底同一套处理。
        var requested = new DateTime(Year, Month, 1);
        var earliest  = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-24);
        var latest    = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
        if (requested < earliest || requested > latest) { Year = DateTime.Today.Year; Month = DateTime.Today.Month; }
        var userId  = CurrentUserId;
        var groupId = CurrentGroupId;

        var records = await attendanceService.GetPersonalAttendanceAsync(new PersonalAttendanceQueryDto
        {
            UserId = userId, Year = Year, Month = Month
        });
        var schedule = await attendanceService.GetMyScheduleAsync(userId, Year, Month);
        var holidays = await attendanceService.GetMonthHolidaysAsync(Year, Month, groupId);

        // 月度汇总平时只在”月初自动生成上个月”或管理员打开月度报表时才会算，本月进行中的汇总一直是空的，
        // 员工自己在日历页也看不到本月概况。这里只在没有汇总行、或汇总行比考勤记录更新时间还旧时才重算
        // （EnsureMonthlySummaryFreshAsync），不是每次打开页面、哪怕数据毫无变化也无条件重算一遍写库；
        // 只传自己的 userId，只重算自己这一条，不是不分青红皂白重算全公司所有人（这里任何登录员工都能到达，
        // year/month 还是 URL 任意指定的，之前不限范围重算全库是个很容易被反复触发的重负载操作）
        await attendanceService.EnsureMonthlySummaryFreshAsync(userId, Year, Month);
        Summary = await attendanceService.GetMonthlySummaryAsync(userId, Year, Month);

        var recByDate     = records.ToDictionary(r => r.WorkDate);
        var shiftByDate    = schedule.ToDictionary(s => s.WorkDate);
        // 同一天可能配了多条假期规则（全公司 + 考勤组专属），调班补班优先展示（它意味着“今天要上班”，比“休息”更值得提醒）
        var holidayByDate  = holidays
            .GroupBy(h => h.Date)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.Type == HolidayType.CompensatoryWorkDay).First());

        var first = new DateOnly(Year, Month, 1);
        var last  = first.AddMonths(1).AddDays(-1);

        // 把“周日=0”的 DayOfWeek 转成“周一开头”的偏移量，补齐第一周前面的空格，让 1 号对齐到正确的星期几
        var leadingBlanks = ((int)first.DayOfWeek + 6) % 7;

        Cells = [];
        for (var i = 0; i < leadingBlanks; i++) Cells.Add(null);
        for (var d = first; d <= last; d = d.AddDays(1))
            Cells.Add(new CalendarCell(d, recByDate.GetValueOrDefault(d), shiftByDate.GetValueOrDefault(d), holidayByDate.GetValueOrDefault(d)));
    }
}
