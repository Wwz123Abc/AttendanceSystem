using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Controllers;

/// <summary>
/// 后台管理接口：员工、部门、考勤组、班次、假期等基础数据的增删改查。
/// 仅「管理员 / 文员」可访问。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
[Route("api/[controller]")]
[ApiController]
public class AdminController(
    IUserService userService,
    AttendanceDbContext db) : ControllerBase
{
    // ── 员工管理 ──────────────────────────────────────────────────────────────

    /// <summary>分页查询员工。</summary>
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int? deptId, [FromQuery] int? groupId,
        [FromQuery] UserRole? role, [FromQuery] string? keyword,
        [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        var (users, total) = await userService.GetUsersAsync(deptId, groupId, role, keyword, pageIndex, pageSize);
        // 只挑前端需要的字段返回（不直接返回整个实体，避免泄露密码哈希等）
        return Ok(new
        {
            Success = true,
            Data = users.Select(u => new
            {
                u.Id, u.EmployeeNo, u.RealName,
                DeptName  = u.Department?.DeptName,
                GroupName = u.AttendanceGroup?.GroupName,
                u.Position, u.Role,
                RoleText  = u.Role.ToDisplayName(),
                u.Phone, u.Email, u.HireDate, u.IsActive, u.LastLoginAt
            }),
            Total = total
        });
    }

    /// <summary>新增员工。</summary>
    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        // 把请求里的字段，组装成一个员工实体
        var user = new User
        {
            EmployeeNo        = req.EmployeeNo,
            RealName          = req.RealName,
            DepartmentId      = req.DepartmentId,
            Position          = req.Position,
            Role              = req.Role,
            AttendanceGroupId = req.AttendanceGroupId,
            SupervisorUserId  = req.SupervisorUserId,
            Phone             = req.Phone,
            Email             = req.Email,
            HireDate          = req.HireDate
        };
        var created = await userService.CreateUserAsync(user, req.InitialPassword);
        return Ok(new { Success = true, Message = "员工创建成功", UserId = created.Id });
    }

    /// <summary>修改员工。</summary>
    [HttpPut("users/{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var user = new User
        {
            Id                = id,
            EmployeeNo        = req.EmployeeNo,
            RealName          = req.RealName,
            DepartmentId      = req.DepartmentId,
            Position          = req.Position,
            Role              = req.Role,
            AttendanceGroupId = req.AttendanceGroupId,
            SupervisorUserId  = req.SupervisorUserId,
            Phone             = req.Phone,
            Email             = req.Email,
            HireDate          = req.HireDate
        };
        var ok = await userService.UpdateUserAsync(user);
        return Ok(new { Success = ok, Message = ok ? "更新成功" : "用户不存在" });
    }

    /// <summary>停用员工（DELETE 在这里表示“停用”，不是真删）。</summary>
    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeactivateUser(int id)
    {
        var ok = await userService.DeactivateUserAsync(id);
        return Ok(new { Success = ok, Message = ok ? "已停用" : "用户不存在" });
    }

    /// <summary>重置某员工密码，返回新密码明文。</summary>
    [HttpPost("users/{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id)
    {
        var newPwd = await userService.ResetPasswordAsync(id);
        return Ok(new { Success = true, Message = "密码已重置", NewPassword = newPwd });
    }

    // ── 部门管理 ──────────────────────────────────────────────────────────────

    /// <summary>查所有启用的部门。</summary>
    [HttpGet("departments")]
    public async Task<IActionResult> GetDepartments()
    {
        var depts = await db.Departments.Where(d => d.IsActive).OrderBy(d => d.Id).ToListAsync();
        return Ok(new { Success = true, Data = depts });
    }

    /// <summary>新增部门。</summary>
    [HttpPost("departments")]
    public async Task<IActionResult> CreateDepartment([FromBody] Department dept)
    {
        dept.CreatedAt = dept.UpdatedAt = DateTime.Now;
        db.Departments.Add(dept);
        await db.SaveChangesAsync();
        return Ok(new { Success = true, DeptId = dept.Id });
    }

    /// <summary>修改部门。</summary>
    [HttpPut("departments/{id:int}")]
    public async Task<IActionResult> UpdateDepartment(int id, [FromBody] Department req)
    {
        var dept = await db.Departments.FindAsync(id);
        if (dept is null) return NotFound();
        dept.DeptName    = req.DeptName;
        dept.DeptCode    = req.DeptCode;
        dept.ParentId    = req.ParentId;
        dept.CompanyName = req.CompanyName;
        dept.Description = req.Description;
        dept.UpdatedAt   = DateTime.Now;
        await db.SaveChangesAsync();
        return Ok(new { Success = true });
    }

    // ── 考勤组管理 ────────────────────────────────────────────────────────────

    /// <summary>查所有启用的考勤组。</summary>
    [HttpGet("attendance-groups")]
    public async Task<IActionResult> GetAttendanceGroups()
    {
        var groups = await db.AttendanceGroups.Where(g => g.IsActive).ToListAsync();
        return Ok(new { Success = true, Data = groups });
    }

    /// <summary>新增考勤组。</summary>
    [HttpPost("attendance-groups")]
    public async Task<IActionResult> CreateAttendanceGroup([FromBody] AttendanceGroup group)
    {
        group.CreatedAt = group.UpdatedAt = DateTime.Now;
        db.AttendanceGroups.Add(group);
        await db.SaveChangesAsync();
        return Ok(new { Success = true, GroupId = group.Id });
    }

    // ── 班次管理 ──────────────────────────────────────────────────────────────

    /// <summary>查班次（可按考勤组过滤）。</summary>
    [HttpGet("shifts")]
    public async Task<IActionResult> GetShifts([FromQuery] int? groupId)
    {
        var q = db.ShiftSchedules.Where(s => s.IsActive).AsQueryable();
        if (groupId.HasValue) q = q.Where(s => s.AttendanceGroupId == groupId.Value);
        return Ok(new { Success = true, Data = await q.ToListAsync() });
    }

    /// <summary>新增班次。</summary>
    [HttpPost("shifts")]
    public async Task<IActionResult> CreateShift([FromBody] ShiftSchedule shift)
    {
        shift.CreatedAt = shift.UpdatedAt = DateTime.Now;
        db.ShiftSchedules.Add(shift);
        await db.SaveChangesAsync();
        return Ok(new { Success = true, ShiftId = shift.Id });
    }

    /// <summary>修改班次。</summary>
    [HttpPut("shifts/{id:int}")]
    public async Task<IActionResult> UpdateShift(int id, [FromBody] ShiftSchedule req)
    {
        var shift = await db.ShiftSchedules.FindAsync(id);
        if (shift is null) return NotFound();
        shift.ShiftName                  = req.ShiftName;
        shift.ShiftType                  = req.ShiftType;
        shift.WorkStartTime              = req.WorkStartTime;
        shift.WorkEndTime                = req.WorkEndTime;
        shift.LateToleranceMinutes       = req.LateToleranceMinutes;
        shift.EarlyLeaveToleranceMinutes = req.EarlyLeaveToleranceMinutes;
        shift.OvertimeThresholdMinutes   = req.OvertimeThresholdMinutes;
        shift.IsCrossDay                 = req.IsCrossDay;
        shift.StandardWorkHours          = req.StandardWorkHours;
        shift.Color                      = req.Color;
        shift.UpdatedAt                  = DateTime.Now;
        await db.SaveChangesAsync();
        return Ok(new { Success = true });
    }

    // ── 假期管理 ──────────────────────────────────────────────────────────────

    /// <summary>查假期（可按年份/考勤组过滤）。</summary>
    [HttpGet("holidays")]
    public async Task<IActionResult> GetHolidays([FromQuery] int? year, [FromQuery] int? groupId)
    {
        var q = db.Holidays.AsQueryable();
        if (year.HasValue)    q = q.Where(h => h.HolidayDate.Year == year.Value);
        if (groupId.HasValue) q = q.Where(h => h.AttendanceGroupId == null || h.AttendanceGroupId == groupId.Value);
        return Ok(new { Success = true, Data = await q.OrderBy(h => h.HolidayDate).ToListAsync() });
    }

    /// <summary>新增假期。</summary>
    [HttpPost("holidays")]
    public async Task<IActionResult> CreateHoliday([FromBody] Holiday holiday)
    {
        holiday.CreatedAt = DateTime.Now;
        db.Holidays.Add(holiday);
        await db.SaveChangesAsync();
        return Ok(new { Success = true, HolidayId = holiday.Id });
    }

    /// <summary>删除假期。</summary>
    [HttpDelete("holidays/{id:int}")]
    public async Task<IActionResult> DeleteHoliday(int id)
    {
        var holiday = await db.Holidays.FindAsync(id);
        if (holiday is null) return NotFound();
        db.Holidays.Remove(holiday);
        await db.SaveChangesAsync();
        return Ok(new { Success = true });
    }
}

// ── 请求模型：装“新增/修改员工”表单字段的简洁数据载体 ──────────────────────────

public record CreateUserRequest(
    string    EmployeeNo,
    string    RealName,
    int?      DepartmentId,
    string?   Position,
    UserRole  Role,
    int?      AttendanceGroupId,
    int?      SupervisorUserId,
    string?   Phone,
    string?   Email,
    DateOnly? HireDate,
    string    InitialPassword);

public record UpdateUserRequest(
    string    EmployeeNo,
    string    RealName,
    int?      DepartmentId,
    string?   Position,
    UserRole  Role,
    int?      AttendanceGroupId,
    int?      SupervisorUserId,
    string?   Phone,
    string?   Email,
    DateOnly? HireDate);
