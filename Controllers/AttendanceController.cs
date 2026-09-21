using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Controllers;

/// <summary>考勤接口：打卡、考勤查询、月度汇总、看板统计。[Authorize] 表示要登录才能用。</summary>
[Authorize]
[Route("api/[controller]")]
[ApiController]
public class AttendanceController(IAttendanceService attendanceService, IDeptScopeService deptScopeService) : ApiControllerBase
{
    // 原来这里有个 POST /api/attendance/punch 接口：只挂了 [Authorize]，不受"远程打卡"那套
    // AllowRemotePunch 开关/人脸识别/地点校验的任何限制——任何登录着的人拿开发者工具直接 POST
    // 这个接口，就能绕开人脸识别伪造打卡。系统里已经没有任何前端页面在调它了（都改走考勤机同步/
    // 远程打卡页面），纯粹是历史遗留的孤儿接口，所以直接删掉。IAttendanceService.PunchAsync 这个
    // 服务方法本身没删——远程打卡页面（Pages/Attendance/RemotePunch.cshtml.cs）还在正常调它。

    /// <summary>查自己今天的考勤状态。</summary>
    [HttpGet("today")]
    public async Task<IActionResult> GetToday()
        => Ok(new { Success = true, Data = await attendanceService.GetTodayAttendanceAsync(CurrentUserId) });

    /// <summary>查自己的考勤记录列表。</summary>
    [HttpGet("personal")]
    public async Task<IActionResult> GetPersonal([FromQuery] PersonalAttendanceQueryDto query)
    {
        query.UserId = CurrentUserId;   // 强制只查自己的，防止查到别人
        var list = await attendanceService.GetPersonalAttendanceAsync(query);
        return Ok(new { Success = true, Data = list, Total = list.Count });
    }

    /// <summary>部门考勤统计（需管理员 / 文员）。</summary>
    [HttpGet("department")]
    [Authorize(Policy = "ManagePolicy")]
    public async Task<IActionResult> GetDepartment([FromQuery] DeptAttendanceQueryDto query)
    {
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetCurrentUser()!);
        var list = await attendanceService.GetDeptAttendanceAsync(query, visibleIds);
        return Ok(new { Success = true, Data = list, Total = list.Count });
    }

    /// <summary>自己的月度汇总。</summary>
    [HttpGet("monthly-summary")]
    public async Task<IActionResult> GetMonthlySummary([FromQuery] int year, [FromQuery] int month)
        => Ok(new { Success = true, Data = await attendanceService.GetMonthlySummaryAsync(CurrentUserId, year, month) });

    /// <summary>部门月度汇总列表（需管理员 / 文员）。</summary>
    [HttpGet("dept-monthly-summary")]
    [Authorize(Policy = "ManagePolicy")]
    public async Task<IActionResult> GetDeptMonthlySummary(
        [FromQuery] int? deptId, [FromQuery] int? groupId,
        [FromQuery] int year, [FromQuery] int month)
    {
        if (month is < 1 or > 12) return BadRequest(new { Success = false, Message = "月份不正确" });
        if (year is < 2000 or > 2100) return BadRequest(new { Success = false, Message = "年份不正确" });

        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetCurrentUser()!);
        var list = await attendanceService.GetDeptMonthlySummariesAsync(deptId, groupId, year, month, visibleIds);
        return Ok(new { Success = true, Data = list, Total = list.Count });
    }

    /// <summary>今日考勤看板（需管理员 / 文员）。</summary>
    [HttpGet("today-stats")]
    [Authorize(Policy = "ManagePolicy")]
    public async Task<IActionResult> GetTodayStats([FromQuery] int? groupId)
    {
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetCurrentUser()!);
        return Ok(new { Success = true, Data = await attendanceService.GetTodayStatsAsync(groupId, visibleIds) });
    }
}
