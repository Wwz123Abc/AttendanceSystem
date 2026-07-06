using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>管理后台首页：显示今日考勤看板（出勤、缺勤、迟到、请假等人数），支持点开卡片查看具体人员。</summary>
[Authorize(Policy = "ManagePolicy")]
public class DashboardModel(IAttendanceService attendanceService) : PageModel
{
    public AttendanceStatsDto Stats { get; set; } = new();   // 看板统计数据

    public async Task OnGetAsync()
        => Stats = await attendanceService.GetTodayStatsAsync();

    /// <summary>
    /// 点开某张统计卡片时，AJAX 请求这里取该类别的具体人员名单。
    /// category：total(总人数)/present(已出勤)/absent(缺勤)/late(迟到)/onleave(请假)/notpunched(未打卡)。
    /// </summary>
    public async Task<JsonResult> OnGetDrilldownAsync(string category)
    {
        var list = await attendanceService.GetTodayStatsDetailAsync(category);
        return new JsonResult(new
        {
            Success = true,
            Data = list.Select(r => new
            {
                r.EmployeeNo,
                r.RealName,
                DeptName = r.DeptName ?? "—",
                ClockIn  = r.ClockInText,
                ClockOut = r.ClockOutText,
                r.StatusText,
                Badge    = ToBadgeClass(r.StatusCssClass)   // 转成本页实际在用的 Bootstrap 徽章色
            })
        });
    }

    /// <summary>把服务层的 f-color-xxx 颜色标记换成 Bootstrap 的 bg-xxx 徽章样式。</summary>
    private static string ToBadgeClass(string cssClass) => cssClass switch
    {
        "f-color-green"  => "bg-success",
        "f-color-orange" => "bg-warning text-dark",
        "f-color-red"    => "bg-danger",
        "f-color-blue"   => "bg-info text-dark",
        "f-color-gray"   => "bg-secondary",
        _                => "bg-secondary"
    };
}
