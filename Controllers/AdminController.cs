using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Controllers;

/// <summary>
/// 后台管理接口：员工、部门、考勤组、班次、假期等基础数据的增删改查。
/// 仅「管理员 / 文员」可访问。分公司管理员（ScopedDepartmentId 有值）只能看到/管理自己范围内的数据——
/// 跟对应的 Razor Page（UserManage/DepartmentManage/GroupManage/ShiftManage/HolidayManage）用同一套
/// IDeptScopeService 过滤逻辑，两边口径必须保持一致，不能只在页面上挡、接口这边没挡。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
[Route("api/[controller]")]
[ApiController]
public class AdminController(
    IUserService userService,
    IDeptScopeService deptScopeService,
    AttendanceDbContext db) : ControllerBase
{
    private CurrentUser Cu => HttpContext.GetCurrentUser()!;

    // ── 员工管理 ──────────────────────────────────────────────────────────────

    /// <summary>分页查询员工。</summary>
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int? deptId, [FromQuery] int? groupId,
        [FromQuery] UserRole? role, [FromQuery] string? keyword,
        [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        deptId = await deptScopeService.ResolveEffectiveDeptIdAsync(Cu, deptId);
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

    /// <summary>新增员工。设备勾选、初始密码规则跟 UserManage 页面保持一致：初始密码固定 123456，
    /// 只推送到 <paramref name="req"/>.DeviceIds 里勾选的设备（不传/传空就是不指定任何设备）。</summary>
    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        if (!await ValidateUserScopeAsync(req.DepartmentId, req.SupervisorUserId, req.Role, req.AttendanceGroupId))
            return Forbid();
        var contactError = ContactValidationHelper.ValidateContactFormat(req.Phone, req.IdNumber);
        if (contactError is not null) return BadRequest(new { Success = false, Message = contactError });
        var deviceIds = req.DeviceIds ?? [];
        if (!await ValidateDeviceScopeAsync(deviceIds))
            return Forbid();

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
            // 归一化跟 UserManage 页面 BuildUser() 一致（大写、去空格），黑名单身份证查重
            // （CreateUserAsync 里 u.IdNumber == user.IdNumber 是精确匹配）才不会因为大小写/空格对不上
            IdNumber          = string.IsNullOrWhiteSpace(req.IdNumber) ? null : req.IdNumber.Trim().ToUpperInvariant(),
            HireDate          = req.HireDate
        };
        // 初始密码统一固定为 123456，跟 UserManage 页面口径一致，不再接受调用方自定义初始密码
        // （首次登录仍强制改密码，见 CreateUserAsync 里 MustChangePassword = true）
        const string initialPwd = "123456";
        var created = await userService.CreateUserAsync(user, initialPwd);
        await userService.SetUserDevicesAsync(created.Id, deviceIds);
        await ApplyScopeAfterSaveAsync(created.Id, req.ScopedDepartmentId);
        return Ok(new { Success = true, Message = "员工创建成功", UserId = created.Id, InitialPassword = initialPwd });
    }

    /// <summary>修改员工。<paramref name="req"/>.DeviceIds 为 null 表示这次不改动设备分配；
    /// 传了（哪怕是空数组）就按传入的集合全量覆盖，跟 UserManage 页面的设备多选框是同一套语义。</summary>
    [HttpPut("users/{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        if (!await CanAccessUserAsync(id)) return Forbid();
        if (!await ValidateUserScopeAsync(req.DepartmentId, req.SupervisorUserId, req.Role, req.AttendanceGroupId))
            return Forbid();
        var contactError = ContactValidationHelper.ValidateContactFormat(req.Phone, req.IdNumber);
        if (contactError is not null) return BadRequest(new { Success = false, Message = contactError });
        if (req.DeviceIds is not null && !await ValidateDeviceScopeAsync(req.DeviceIds))
            return Forbid();

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
            IdNumber          = string.IsNullOrWhiteSpace(req.IdNumber) ? null : req.IdNumber.Trim().ToUpperInvariant(),
            HireDate          = req.HireDate
        };
        var ok = await userService.UpdateUserAsync(user);
        if (ok)
        {
            if (req.DeviceIds is not null)
                await userService.SetUserDevicesAsync(id, req.DeviceIds);
            await ApplyScopeAfterSaveAsync(id, req.ScopedDepartmentId);
        }
        return Ok(new { Success = ok, Message = ok ? "更新成功" : "用户不存在" });
    }

    /// <summary>停用员工（DELETE 在这里表示“停用”，不是真删）。</summary>
    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeactivateUser(int id)
    {
        if (!await CanAccessUserAsync(id)) return Forbid();
        var ok = await userService.DeactivateUserAsync(id);
        return Ok(new { Success = ok, Message = ok ? "已停用" : "用户不存在" });
    }

    /// <summary>重置某员工密码，返回新密码明文。</summary>
    [HttpPost("users/{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id)
    {
        if (!await CanAccessUserAsync(id)) return Forbid();
        var newPwd = await userService.ResetPasswordAsync(id);
        return Ok(new { Success = true, Message = "密码已重置", NewPassword = newPwd });
    }

    /// <summary>目标员工是否在当前登录者的管理范围内（按目标员工的 DepartmentId 判断）。</summary>
    private async Task<bool> CanAccessUserAsync(int userId)
    {
        var deptId = await db.Users.Where(u => u.Id == userId).Select(u => (int?)u.DepartmentId).FirstOrDefaultAsync();
        return await deptScopeService.CanAccessDeptAsync(Cu, deptId);
    }

    /// <summary>考勤机不做管理范围校验（2026-09-21 按业务要求取消隔离，方便员工借调到其他分公司时
    /// 直接推送到对方的考勤机），跟 UserManage 页面同口径——只确认设备真的存在且启用，不存在的 id
    /// 会查出 null，不然会一路走到 SetUserDevicesAsync 插入 UserZKDevice 时才撞外键约束报错（500）。</summary>
    private async Task<bool> ValidateDeviceScopeAsync(IEnumerable<int> deviceIds)
    {
        var idSet = deviceIds.Distinct().ToList();
        if (idSet.Count == 0) return true;
        var validCount = await db.ZKDevices.CountAsync(d => idSet.Contains(d.Id) && d.IsActive);
        return validCount == idSet.Count;
    }

    /// <summary>
    /// 新建/编辑保存后，落定这个账号的"管理范围"（ScopedDepartmentId）：总部超级管理员（角色=Admin 且
    /// 自己不受范围限制）可以自由指定 <paramref name="requestedScope"/>（含清空=设为不受限）；
    /// 分公司管理员/文员调这个接口时，不管传什么范围值都会被忽略，强制钳到操作者自己当前的范围。
    /// 跟 UserManage 页面的 ApplyScopeAfterSaveAsync 是同一套规则——之前这个接口完全没有设置
    /// ScopedDepartmentId 的代码，等于不管谁调用，走 API 建的新账号 ScopedDepartmentId 恒为 null，
    /// null 在 DeptScopeService 里的语义是"不受限"，是一条比页面那条更彻底的越权提权链。
    /// </summary>
    private async Task ApplyScopeAfterSaveAsync(int userId, int? requestedScope)
    {
        var isHqSuperAdmin = Cu.Role == UserRole.Admin && !Cu.IsScoped;
        await userService.SetScopedDepartmentAsync(userId, isHqSuperAdmin ? requestedScope : Cu.ScopedDepartmentId);
    }

    /// <summary>新建/编辑员工前的范围+权限校验：部门、直属上级必须在管理范围内；只有不受限的
    /// 总部管理员才能把角色设成管理员——跟 UserManage 页面用的是同一套规则。</summary>
    private async Task<bool> ValidateUserScopeAsync(int? deptId, int? supervisorUserId, UserRole role, int? groupId = null)
    {
        if (!await deptScopeService.CanAccessDeptAsync(Cu, deptId)) return false;
        // 考勤组归属校验：原来只校验了部门/上级/角色/设备，唯独漏了考勤组——受限管理员能通过这个接口
        // 把员工挂到任意考勤组（含别的分公司的组），套用对方的班次时间/休息日/扣时规则，
        // 跟 UserManage 页面的 IsGroupInScopeAsync 是同一套口径（2026-09-17 代码审查发现）
        if (groupId.HasValue && !await IsGroupInScopeAsync(groupId.Value)) return false;
        if (supervisorUserId.HasValue)
        {
            var superDeptId = await db.Users.Where(u => u.Id == supervisorUserId.Value)
                .Select(u => (int?)u.DepartmentId).FirstOrDefaultAsync();
            if (!await deptScopeService.CanAccessDeptAsync(Cu, superDeptId)) return false;
        }
        // 只有"总部超级管理员"（角色=Admin 且自己不受范围限制）才能把别人的角色设成管理员——原来这里
        // 只判断了 Cu.IsScoped，一个"没被设置范围、但角色只是文员"的账号 IsScoped 恒为 false，
        // 光挡"受限"挡不住这种账号把自己或别人提权成 Admin
        if (!(Cu.Role == UserRole.Admin && !Cu.IsScoped) && role == UserRole.Admin) return false;
        return true;
    }

    // ── 部门管理 ──────────────────────────────────────────────────────────────

    /// <summary>查所有启用的部门。</summary>
    [HttpGet("departments")]
    public async Task<IActionResult> GetDepartments()
    {
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(Cu);
        var q = db.Departments.Where(d => d.IsActive);
        if (visibleIds is not null) q = q.Where(d => visibleIds.Contains(d.Id));
        // 只投影需要的字段，不直接把实体序列化返回——Department 带双向导航
        // （ParentDepartment ↔ ChildDepartments），有真实多级部门数据时 EF 会自动把同一次查询里
        // 加载到的多条 Department 互相接上导航，JSON 序列化遇到这种循环引用直接 500。
        // 之前测试数据全是没有父子关系的扁平部门，侥幸没触发；跟 GetUsers 接口一样改成只挑字段的
        // 投影，从根上避免，不依赖"数据碰巧没有层级"这种脆弱前提。
        var depts = await q.OrderBy(d => d.Id)
            .Select(d => new
            {
                d.Id, d.DeptName, d.DeptCode, d.ParentId, d.CompanyName, d.Description,
                d.AttendanceGroupId, d.IsActive, d.SortIndex
            })
            .ToListAsync();
        return Ok(new { Success = true, Data = depts });
    }

    /// <summary>新增部门。</summary>
    [HttpPost("departments")]
    public async Task<IActionResult> CreateDepartment([FromBody] Department dept)
    {
        if (Cu.IsScoped && !dept.ParentId.HasValue) return Forbid();
        if (!await deptScopeService.CanAccessDeptAsync(Cu, dept.ParentId)) return Forbid();

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
        if (!await deptScopeService.CanAccessDeptAsync(Cu, dept.Id)) return Forbid();

        if (Cu.IsScoped && dept.Id == Cu.ScopedDepartmentId!.Value)
        {
            if (req.ParentId != dept.ParentId) return Forbid();   // 不能改自己范围根部门的上级
        }
        else
        {
            if (Cu.IsScoped && !req.ParentId.HasValue) return Forbid();
            if (!await deptScopeService.CanAccessDeptAsync(Cu, req.ParentId)) return Forbid();
        }

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
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(Cu);
        // 同 GetDepartments：不能 Include(g => g.Departments) 再把整个 AttendanceGroup 实体序列化——
        // AttendanceGroup.Departments 和 Department.AttendanceGroup 是双向导航，会被 EF 自动互相接上，
        // 真实数据下 JSON 序列化直接 500。改成只投影需要的字段，跟随部门只取 Id 列表（子查询），
        // 不加载 Department 实体本身，从根上不会产生循环引用。
        var all = await db.AttendanceGroups.Where(g => g.IsActive)
            .Select(g => new
            {
                g.Id, g.GroupName, g.CompanyName, g.ClerkUserId, g.ApprovalLevel,
                g.EnableLocationPunch, g.LunchBreakMinutes, g.DinnerBreakMinutes, g.IsActive,
                DepartmentIds = g.Departments.Select(d => d.Id).ToList()
            })
            .ToListAsync();
        var groups = visibleIds is null
            ? all
            : all.Where(g => g.DepartmentIds.Count == 0 || g.DepartmentIds.Any(visibleIds.Contains)).ToList();
        return Ok(new { Success = true, Data = groups });
    }

    /// <summary>新增考勤组。只有总部超级管理员能用这个接口——它是直接绑整个 AttendanceGroup 实体
    /// （含嵌套的 Departments/Approvers 导航集合）落库，不像"考勤组管理"页面那样对受限管理员做过
    /// "勾选部门/审批人必须落在自己范围内"的校验，也没有"零部门通用组只有总部能建"这类口径限制；
    /// 分公司管理员要建考勤组，请走"考勤组管理"页面（有完整校验），不要放开给这个接口。</summary>
    [HttpPost("attendance-groups")]
    public async Task<IActionResult> CreateAttendanceGroup([FromBody] AttendanceGroup group)
    {
        // 只判 IsScoped 挡不住"没被设置范围、但角色只是文员"的账号（IsScoped 恒为 false）——
        // 跟 ValidateUserScopeAsync（:221）、ApplyScopeAfterSaveAsync（:199）用同一套口径
        if (Cu.IsScoped || Cu.Role != UserRole.Admin) return Forbid();

        group.CreatedAt = group.UpdatedAt = DateTime.Now;
        db.AttendanceGroups.Add(group);
        await db.SaveChangesAsync();
        return Ok(new { Success = true, GroupId = group.Id });
    }

    /// <summary>某个考勤组是否在当前登录者的管理范围内（口径跟 GroupManage 页一致）。给"查看/使用"这种
    /// 只读场景用；完全没关联部门的全局/共享考勤组对所有人可见。</summary>
    private async Task<bool> IsGroupInScopeAsync(int groupId)
    {
        if (!Cu.IsScoped) return true;
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return true;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(Cu);
        return deptIds.Any(id => visibleIds!.Contains(id));
    }

    /// <summary>某个考勤组是否允许当前登录者"写"（新增/修改/删除挂在该组下的班次、假期等配置）：
    /// 不受限一律可以；受限管理员只能写"关联部门都在自己范围内"的组——完全没关联部门的全局/共享
    /// 考勤组（多个分公司可能都在用）只有总部管理员能写，避免分公司管理员改动了别的分公司也在用的
    /// 共用配置（可见，但不能改，跟 IsGroupInScopeAsync 的口径区分开）。</summary>
    private async Task<bool> IsGroupWritableAsync(int groupId)
    {
        if (!Cu.IsScoped) return true;
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return false;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(Cu);
        // 必须是"关联部门都在自己范围内"才能写（All，不是 Any）——如果一个组同时挂了 A、B 两家公司的部门，
        // 只要 Any 命中就放行的话，A、B 两边的受限管理员会都能改/停/删这个"跨司共用组"，形同没有隔离
        return deptIds.All(id => visibleIds!.Contains(id));
    }

    // ── 班次管理 ──────────────────────────────────────────────────────────────

    /// <summary>查班次（可按考勤组过滤）。</summary>
    [HttpGet("shifts")]
    public async Task<IActionResult> GetShifts([FromQuery] int? groupId)
    {
        var q = db.ShiftSchedules.Where(s => s.IsActive).AsQueryable();
        if (groupId.HasValue) q = q.Where(s => s.AttendanceGroupId == groupId.Value);
        // 同 GetDepartments/GetAttendanceGroups：不能直接把 ShiftSchedule 实体序列化返回——
        // ShiftSchedule.AttendanceGroup 是导航属性，下面 Cu.IsScoped 分支又会在同一个 DbContext 里
        // Include(g => g.Departments) 加载 AttendanceGroup，EF 会自动把两边导航互相接上，
        // 序列化时一样会因为 AttendanceGroup↔Department 的循环引用炸掉——跟受限管理员访问
        // GetDepartments/GetAttendanceGroups 是同一个根因，这里只是触发路径不同。改成只投影字段。
        var shifts = await q
            .Select(s => new
            {
                s.Id, s.AttendanceGroupId, s.ShiftName, s.ShiftType, s.WorkStartTime, s.WorkEndTime,
                s.LateToleranceMinutes, s.EarlyLeaveToleranceMinutes, s.EarliestClockInMinutes,
                s.OvertimeThresholdMinutes, s.MidCheckWindows, s.IsCrossDay, s.StandardWorkHours,
                s.Color, s.IsActive, s.RestDaysOfWeek
            })
            .ToListAsync();
        if (Cu.IsScoped)
        {
            var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(Cu);
            var allowedGroupIds = (await db.AttendanceGroups.Include(g => g.Departments).ToListAsync())
                .Where(g => g.Departments.Count == 0 || g.Departments.Any(d => visibleIds!.Contains(d.Id)))
                .Select(g => g.Id).ToHashSet();
            shifts = shifts.Where(s => allowedGroupIds.Contains(s.AttendanceGroupId)).ToList();
        }
        return Ok(new { Success = true, Data = shifts });
    }

    /// <summary>新增班次。用 DTO 而不是直接绑 ShiftSchedule 实体：ShiftSchedule.AttendanceGroup 是
    /// 非空导航属性（真正外键是 AttendanceGroupId），[ApiController] 会把它当成"必填字段"校验，
    /// 调用方不传一个完整的 attendanceGroup 嵌套对象就直接 400——调用方只应该传 attendanceGroupId
    /// 这个外键值，不该被要求传导航对象。</summary>
    [HttpPost("shifts")]
    public async Task<IActionResult> CreateShift([FromBody] CreateShiftRequest req)
    {
        if (!await IsGroupWritableAsync(req.AttendanceGroupId)) return Forbid();
        // 非跨天班次，下班时间必须晚于上班时间——不然会配出一个 08:00~08:00 甚至反过来的班次，
        // 后面算迟到/早退/工时全部会跟着算错，但当时不会报任何错，很难排查
        if (!req.IsCrossDay && req.WorkEndTime <= req.WorkStartTime)
            return BadRequest(new { Success = false, Message = "非跨天班次的下班时间必须晚于上班时间" });
        var shift = new ShiftSchedule
        {
            AttendanceGroupId          = req.AttendanceGroupId,
            ShiftName                  = req.ShiftName,
            ShiftType                  = req.ShiftType,
            WorkStartTime              = req.WorkStartTime,
            WorkEndTime                = req.WorkEndTime,
            LateToleranceMinutes       = req.LateToleranceMinutes,
            EarlyLeaveToleranceMinutes = req.EarlyLeaveToleranceMinutes,
            EarliestClockInMinutes     = req.EarliestClockInMinutes,
            OvertimeThresholdMinutes   = req.OvertimeThresholdMinutes,
            IsCrossDay                 = req.IsCrossDay,
            StandardWorkHours          = req.StandardWorkHours,
            Color                      = req.Color,
            RestDaysOfWeek             = req.RestDaysOfWeek,
            MidCheckWindows            = req.MidCheckWindows,
            IsActive                   = true,
            CreatedAt                  = DateTime.Now,
            UpdatedAt                  = DateTime.Now
        };
        db.ShiftSchedules.Add(shift);
        await db.SaveChangesAsync();
        return Ok(new { Success = true, ShiftId = shift.Id });
    }

    /// <summary>修改班次。同 CreateShift，用 DTO 避免 ShiftSchedule.AttendanceGroup 导航属性
    /// 被当成必填字段。不支持通过这个接口把班次挪到别的考勤组（跟改动前的行为一致，
    /// 沿用哪个组由创建时的 AttendanceGroupId 决定）。</summary>
    [HttpPut("shifts/{id:int}")]
    public async Task<IActionResult> UpdateShift(int id, [FromBody] UpdateShiftRequest req)
    {
        var shift = await db.ShiftSchedules.FindAsync(id);
        if (shift is null) return NotFound();
        if (!await IsGroupWritableAsync(shift.AttendanceGroupId)) return Forbid();
        if (!req.IsCrossDay && req.WorkEndTime <= req.WorkStartTime)
            return BadRequest(new { Success = false, Message = "非跨天班次的下班时间必须晚于上班时间" });

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
        // 这三个是"部分更新"语义（跟上面几个字段不一样）：null=本次不改这个字段，不会把老调用方
        // （没传新字段）的班次悄悄重置成默认值；RestDaysOfWeek/MidCheckWindows 传了就整体覆盖，
        // 跟 ShiftManage 页面保存时的语义一致——MidCheckWindows 传空串表示"清空窗口"，
        // 因为 null 已经被"不改"占用了，清空必须显式传空串区分开
        if (req.EarliestClockInMinutes.HasValue) shift.EarliestClockInMinutes = req.EarliestClockInMinutes.Value;
        if (req.RestDaysOfWeek is not null && System.Text.RegularExpressions.Regex.IsMatch(req.RestDaysOfWeek, @"^[0-6](,[0-6])*$"))
            shift.RestDaysOfWeek = req.RestDaysOfWeek;
        if (req.MidCheckWindows is not null) shift.MidCheckWindows = req.MidCheckWindows == "" ? null : req.MidCheckWindows;
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
        // 同 GetShifts：不能直接把 Holiday 实体序列化返回，下面 Cu.IsScoped 分支会在同一个
        // DbContext 里 Include(g => g.Departments) 加载 AttendanceGroup，EF 自动把 Holiday.AttendanceGroup
        // 和 AttendanceGroup.Departments↔Department.AttendanceGroup 都接上导航，序列化会循环引用炸掉。
        var holidays = await q.OrderBy(h => h.HolidayDate)
            .Select(h => new
            {
                h.Id, h.HolidayName, h.HolidayDate, h.HolidayType, h.AttendanceGroupId, h.Description, h.CreatedAt
            })
            .ToListAsync();
        if (Cu.IsScoped)
        {
            var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(Cu);
            var allowedGroupIds = (await db.AttendanceGroups.Include(g => g.Departments).ToListAsync())
                .Where(g => g.Departments.Count == 0 || g.Departments.Any(d => visibleIds!.Contains(d.Id)))
                .Select(g => g.Id).ToHashSet();
            holidays = holidays.Where(h => h.AttendanceGroupId is null || allowedGroupIds.Contains(h.AttendanceGroupId.Value)).ToList();
        }
        return Ok(new { Success = true, Data = holidays });
    }

    /// <summary>新增假期。</summary>
    [HttpPost("holidays")]
    public async Task<IActionResult> CreateHoliday([FromBody] Holiday holiday)
    {
        if (Cu.IsScoped)
        {
            if (!holiday.AttendanceGroupId.HasValue) return Forbid();   // 受限管理员不能建全公司通用假期
            if (!await IsGroupWritableAsync(holiday.AttendanceGroupId.Value)) return Forbid();
        }
        // 同一天、同一个考勤组范围（或都是全公司通用）不能重复配置，不然假期列表里会出现看起来
        // 一模一样、又没法区分该删哪条的重复项
        if (await db.Holidays.AnyAsync(h => h.HolidayDate == holiday.HolidayDate && h.AttendanceGroupId == holiday.AttendanceGroupId))
            return BadRequest(new { Success = false, Message = "这一天已经配置过假期，不能重复添加" });
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
        var allowed = holiday.AttendanceGroupId.HasValue
            ? await IsGroupWritableAsync(holiday.AttendanceGroupId.Value)
            : !Cu.IsScoped;
        if (!allowed) return Forbid();

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
    // 身份证号：必须能通过这个接口存上，否则黑名单身份证查重（CreateUserAsync 里按
    // IdNumber 精确匹配）对走 API 建档的员工完全不生效，等于形同虚设
    string?   IdNumber,
    DateOnly? HireDate,
    // 这个人要推送到哪几台考勤机（不传/传空 = 不指定任何设备）；初始密码不再由调用方指定，
    // 统一固定为 123456，见 CreateUser 方法内部
    List<int>? DeviceIds = null,
    // 管理范围：只有总部超级管理员的这个值会被采纳（null=不受限）；分公司管理员/文员调这个接口时
    // 传什么都会被忽略，新账号强制钳到操作者自己的范围，见 CreateUser 方法内部
    int?       ScopedDepartmentId = null);

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
    string?   IdNumber,
    DateOnly? HireDate,
    // null = 这次不改动设备分配；传了（哪怕空数组）就按传入集合全量覆盖
    List<int>? DeviceIds = null,
    int?       ScopedDepartmentId = null);

// ── 请求模型：装"新增/修改班次"表单字段的简洁数据载体 ──────────────────────────
// 不直接绑 ShiftSchedule 实体：它的 AttendanceGroup 导航属性是非空引用类型，
// [ApiController] 会把没传这个字段的请求当成校验失败直接 400，调用方只该传 AttendanceGroupId。

public record CreateShiftRequest(
    int       AttendanceGroupId,
    string    ShiftName,
    TimeOnly  WorkStartTime,
    TimeOnly  WorkEndTime,
    ShiftType ShiftType = ShiftType.Fixed,
    int       LateToleranceMinutes = 5,
    int       EarlyLeaveToleranceMinutes = 5,
    int       EarliestClockInMinutes = 60,
    int       OvertimeThresholdMinutes = 30,
    bool      IsCrossDay = false,
    decimal   StandardWorkHours = 8,
    string    Color = "#1890ff",
    string    RestDaysOfWeek = "0,6",
    string?   MidCheckWindows = null);

public record UpdateShiftRequest(
    string    ShiftName,
    ShiftType ShiftType,
    TimeOnly  WorkStartTime,
    TimeOnly  WorkEndTime,
    int       LateToleranceMinutes,
    int       EarlyLeaveToleranceMinutes,
    int       OvertimeThresholdMinutes,
    bool      IsCrossDay,
    decimal   StandardWorkHours,
    string    Color,
    // 新增（可空：null = 本次不修改该字段；传值 = 覆盖）
    int?      EarliestClockInMinutes = null,   // 最多提前打卡分钟数
    string?   RestDaysOfWeek = null,           // 每周休息日，如 "0,6"（0=周日…6=周六）
    string?   MidCheckWindows = null);         // 午间必打卡窗口（格式同 Create 接口；""=清空）
