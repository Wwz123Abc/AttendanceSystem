using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Controllers;

/// <summary>考勤接口：打卡、考勤查询、月度汇总、看板统计。[Authorize] 表示要登录才能用。</summary>
[Authorize]
[Route("api/[controller]")]
[ApiController]
public class AttendanceController(IAttendanceService attendanceService) : ApiControllerBase
{
    /// <summary>员工打卡（上班 / 下班）。</summary>
    [HttpPost("punch")]
    public async Task<IActionResult> Punch([FromBody] PunchRequestDto req)
    {
        var result = await attendanceService.PunchAsync(CurrentUserId, req);   // CurrentUserId=当前登录的人
        return Ok(result);
    }

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
        var list = await attendanceService.GetDeptAttendanceAsync(query);
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
        var list = await attendanceService.GetDeptMonthlySummariesAsync(deptId, groupId, year, month);
        return Ok(new { Success = true, Data = list, Total = list.Count });
    }

    /// <summary>今日考勤看板（需管理员 / 文员）。</summary>
    [HttpGet("today-stats")]
    [Authorize(Policy = "ManagePolicy")]
    public async Task<IActionResult> GetTodayStats([FromQuery] int? groupId)
        => Ok(new { Success = true, Data = await attendanceService.GetTodayStatsAsync(groupId) });
}
