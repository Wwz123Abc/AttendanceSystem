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
        Year  = year  ?? DateTime.Today.Year;
        Month = month ?? DateTime.Today.Month;
        var userId = CurrentUserId;

        Records = await attendanceService.GetPersonalAttendanceAsync(new PersonalAttendanceQueryDto
        {
            UserId = userId,
            Year   = Year,
            Month  = Month
        });
        Summary = await attendanceService.GetMonthlySummaryAsync(userId, Year, Month);
    }
}
