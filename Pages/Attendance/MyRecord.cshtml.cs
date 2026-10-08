using Microsoft.AspNetCore.Authorization;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Attendance;

/// <summary>
/// 我的考勤页：按薪资周期（上月 26 日 ~ 本月 25 日，见 <see cref="PayrollCycle"/>）显示个人每日明细和汇总。
/// 汇总数字（含总工时）取自"发薪考勤汇总表"同一套算法，员工看到的总工时和发薪汇总表一致。
/// </summary>
[Authorize]
public class MyRecordModel(IAttendanceService attendanceService) : AppPageModel
{
    public List<AttendanceRecordDto> Records { get; set; } = [];   // 每日明细

    /// <summary>这个周期的发薪汇总行；免考勤账号没有，页面按 0 显示。</summary>
    public TemplateReportRowDto? Payroll { get; set; }

    /// <summary>周期的结束年月（10 月周期 = 9/26~10/25，Year=2026、Month=10）。</summary>
    public int Year  { get; set; }
    public int Month { get; set; }
    public DateOnly CycleStart { get; set; }
    public DateOnly CycleEnd   { get; set; }

    /// <summary>打开页面时按周期加载数据（不传年月就用今天所在的周期）。</summary>
    public async Task OnGetAsync(int? year, int? month, CancellationToken ct)
    {
        (Year, Month) = PayrollCycle.ResolveRequested(year, month, DateOnly.FromDateTime(DateTime.Today));
        (CycleStart, CycleEnd) = PayrollCycle.Range(Year, Month);
        var userId = CurrentUserId;

        Records = await attendanceService.GetPersonalAttendanceAsync(new PersonalAttendanceQueryDto
        {
            UserId = userId, StartDate = CycleStart, EndDate = CycleEnd
        });
        Payroll = await attendanceService.GetMyPayrollRowAsync(userId, CycleStart, CycleEnd, ct);
    }
}
