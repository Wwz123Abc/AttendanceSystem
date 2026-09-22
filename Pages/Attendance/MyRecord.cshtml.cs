using Microsoft.AspNetCore.Authorization;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Attendance;

/// <summary>我的考勤页：按月显示个人的每日明细和月度汇总。</summary>
[Authorize]
public class MyRecordModel(IAttendanceService attendanceService) : AppPageModel
{
    public List<AttendanceRecordDto> Records { get; set; } = [];   // 每日明细
    public MonthlySummaryDto?        Summary { get; set; }         // 月度汇总

    public int Year  { get; set; }
    public int Month { get; set; }

    /// <summary>打开页面时按年月加载数据（不传年月就用当前年月）。</summary>
    public async Task OnGetAsync(int? year, int? month)
    {
        // year/month 直接来自 URL，跟"我的日历"同样的兜底（非法值一律当成没传，退回当前年月）
        Year  = year  is >= 2000 and <= 2100 ? year.Value : DateTime.Today.Year;
        Month = month is >= 1 and <= 12 ? month.Value : DateTime.Today.Month;
        // 只放开"最近 24 个月以内（含下个月）"，超出范围退回当前年月——跟"我的日历"同一套限制，
        // 避免遍历任意合法年月触发大量 EnsureMonthlySummaryFreshAsync 重算/写库
        var requested = new DateTime(Year, Month, 1);
        var earliest  = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-24);
        var latest    = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
        if (requested < earliest || requested > latest) { Year = DateTime.Today.Year; Month = DateTime.Today.Month; }
        var userId = CurrentUserId;

        Records = await attendanceService.GetPersonalAttendanceAsync(new PersonalAttendanceQueryDto
        {
            UserId = userId,
            Year   = Year,
            Month  = Month
        });
        // 跟"我的日历"用同一个方法保证两页表现一致：没有汇总行就生成一份，不能让这个月还没生成过
        // 汇总时，"我的日历"因为强制重算总有数据、"我的记录"却是空白（2026-09-21 代码审查发现）
        await attendanceService.EnsureMonthlySummaryFreshAsync(userId, Year, Month);
        Summary = await attendanceService.GetMonthlySummaryAsync(userId, Year, Month);
    }
}
