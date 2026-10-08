using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>员工建档、改资料、改角色、批量启停的执行（<see cref="AgentActionService"/> 的一部分）。</summary>
public partial class AgentActionService
{
    // ── 员工建档 / 改资料 / 批量启停 / 假期增删（执行侧）────────────────────────

    private static List<int> IntListArg(JsonElement args, string name)
    {
        var list = new List<int>();
        if (args.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number) list.Add(v.GetInt32());
        return list;
    }

    /// <summary>读取某部门所有上级候选校验同一人（主管/班组长 且同部门）。</summary>
    private async Task<(bool ok, string? err)> ValidateSupervisorAsync(int supervisorId, int deptId)
    {
        var sup = await db.Users.AsNoTracking()
            .Where(u => u.Id == supervisorId)
            .Select(u => new { u.IsActive, u.Role, u.DepartmentId, u.RealName })
            .FirstOrDefaultAsync();
        if (sup is null || !sup.IsActive || sup.Role is not (AttendanceSystem.Models.Enums.UserRole.Supervisor or AttendanceSystem.Models.Enums.UserRole.TeamLeader))
            return (false, "直属上级必须是在职的主管/班组长");
        if (sup.DepartmentId != deptId) return (false, "直属上级必须和目标员工在同一个部门");
        return (true, null);
    }

    private async Task<(bool, string)> ExecuteCreateEmployeeAsync(int operatorUserId, JsonElement args)
    {
        var realName = GetString(args, "realName")?.Trim();
        var deptId   = GetInt(args, "deptId");
        var supId    = GetInt(args, "supervisorId");
        var phone    = GetString(args, "phone")?.Trim();
        var employeeNo = GetString(args, "employeeNo")?.Trim();
        var position = GetString(args, "position")?.Trim();
        var contract = GetString(args, "contractCompany")?.Trim();
        var hireS    = GetString(args, "hireDate")?.Trim();

        if (string.IsNullOrEmpty(realName) || !deptId.HasValue || !supId.HasValue)
            return (false, "参数不完整：realName/deptId/supervisorId");
        if (string.IsNullOrEmpty(phone) || !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^1[3-9]\d{9}$"))
            return (false, "手机号格式不正确");

        var (_, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);
        var deptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync();
        if (deptName is null) return (false, "归属部门不存在");
        if (visibleIds is not null && !visibleIds.Contains(deptId.Value)) return (false, "归属部门不在你的管理范围内");
        if (supId.Value == operatorUserId) return (false, "直属上级不能是自己");
        var (vok, verr) = await ValidateSupervisorAsync(supId.Value, deptId.Value);
        if (!vok) return (false, verr!);

        string finalEno;
        if (!string.IsNullOrEmpty(employeeNo))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(employeeNo, @"^[A-Za-z0-9_-]{1,50}$"))
                return (false, "工号只能包含字母/数字/下划线/短横线");
            if (await userService.IsEmployeeNoExistsAsync(employeeNo)) return (false, $"工号 {employeeNo} 已被占用");
            finalEno = employeeNo;
        }
        else
        {
            finalEno = await userService.GenerateNextEmployeeNoAsync(deptId.Value)
                       ?? throw new BusinessException("该部门无法自动生成工号，请手动填写");
        }

        DateOnly? hireDate = null;
        if (!string.IsNullOrEmpty(hireS))
        {
            if (!DateOnly.TryParse(hireS, out var hd)) return (false, "hireDate 格式不正确");
            hireDate = hd;
        }

        var followedGroupId = await groupService.GetGroupIdForDepartmentAsync(deptId.Value);
        var user = new Models.Entities.User
        {
            EmployeeNo        = finalEno,
            RealName          = realName!,
            Role              = AttendanceSystem.Models.Enums.UserRole.Employee,
            DepartmentId      = deptId.Value,
            AttendanceGroupId = followedGroupId,
            SupervisorUserId  = supId.Value,
            Position          = string.IsNullOrEmpty(position) ? null : position,
            ContractCompany   = string.IsNullOrEmpty(contract) ? null : contract,
            Phone             = phone,
            HireDate          = hireDate,
            AllowRemotePunch  = false,
            IsActive          = true,
            CreatedAt         = clock.LocalNow(),
            UpdatedAt         = clock.LocalNow()
        };
        await userService.CreateUserAsync(user, "123456");
        return (true, $"已新建员工 {realName}（{finalEno}），部门【{deptName}】。初始密码 123456（系统不会强制改密，请提醒本人自行修改）");
    }

    private async Task<(bool, string)> ExecuteUpdateEmployeeAsync(int operatorUserId, JsonElement args)
    {
        var userId = GetInt(args, "userId");
        if (!userId.HasValue) return (false, "参数缺失：userId");

        var (cu, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);

        var user = await db.Users.FindAsync(userId.Value);
        if (user is null) return (false, "目标员工不存在");
        if (visibleIds is not null && (user.DepartmentId is null || !visibleIds.Contains(user.DepartmentId.Value)))
            return (false, "该员工不在你的管理范围内");
        if (userId.Value == operatorUserId && GetInt(args, "deptId").HasValue)
            return (false, "不能修改自己的部门归属");

        var realName = GetString(args, "realName")?.Trim();
        var phone    = GetString(args, "phone")?.Trim();
        var position = GetString(args, "position")?.Trim();
        var contract = GetString(args, "contractCompany")?.Trim();
        var hireS    = GetString(args, "hireDate")?.Trim();
        var deptId   = GetInt(args, "deptId");
        var supId    = GetInt(args, "supervisorId");

        if (phone is not null && !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^1[3-9]\d{9}$"))
            return (false, "手机号格式不正确");

        if (realName is not null) user.RealName = realName;
        if (phone is not null) user.Phone = phone;
        if (position is not null) user.Position = string.IsNullOrEmpty(position) ? null : position;
        if (contract is not null) user.ContractCompany = string.IsNullOrEmpty(contract) ? null : contract;
        if (!string.IsNullOrEmpty(hireS))
        {
            if (!DateOnly.TryParse(hireS, out var hd)) return (false, "hireDate 格式不正确");
            user.HireDate = hd;
        }

        if (deptId.HasValue)
        {
            var newDeptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync();
            if (newDeptName is null) return (false, "新部门不存在");
            if (visibleIds is not null && !visibleIds.Contains(deptId.Value)) return (false, "新部门不在你的管理范围内");
            user.DepartmentId = deptId.Value;
            user.AttendanceGroupId = await groupService.GetGroupIdForDepartmentAsync(deptId.Value);   // 部门跟随考勤组
        }
        if (supId.HasValue)
        {
            var finalDept = user.DepartmentId;
            if (!finalDept.HasValue) return (false, "目标员工未分部门，不能设置直属上级");
            if (supId.Value == user.Id) return (false, "直属上级不能是自己");
            var (vok, verr) = await ValidateSupervisorAsync(supId.Value, finalDept.Value);
            if (!vok) return (false, verr!);
            user.SupervisorUserId = supId.Value;
        }

        user.UpdatedAt = clock.LocalNow();
        await userService.UpdateUserAsync(user, operatorUserId);
        return (true, $"已更新员工 {user.RealName}（{user.EmployeeNo}）的资料");
    }

    private async Task<(bool, string)> ExecuteChangeRoleAsync(int operatorUserId, JsonElement args)
    {
        var uid   = GetInt(args, "userId");
        var roleS = GetString(args, "role")?.ToLowerInvariant();   // 存的参数已统一小写，这里再兜底一次不怕旧格式的待处理动作
        if (!uid.HasValue || uid.Value <= 0) return (false, "参数缺失：userId");
        AttendanceSystem.Models.Enums.UserRole? targetRole = roleS switch
        {
            "employee"   => AttendanceSystem.Models.Enums.UserRole.Employee,
            "clerk"      => AttendanceSystem.Models.Enums.UserRole.Clerk,
            "supervisor" => AttendanceSystem.Models.Enums.UserRole.Supervisor,
            "teamleader" => AttendanceSystem.Models.Enums.UserRole.TeamLeader,
            "admin"      => AttendanceSystem.Models.Enums.UserRole.Admin,
            _            => null
        };
        if (!targetRole.HasValue) return (false, "role 不合法");

        var (cu, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);
        if (uid.Value == operatorUserId) return (false, "不能调整自己的角色");

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == uid.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        if (target is null) return (false, "目标员工不存在");
        if (visibleIds is not null && (target.DepartmentId is null || !visibleIds.Contains(target.DepartmentId.Value)))
            return (false, "该员工不在你的管理范围内");

        // admin/clerk 仅总部（不受限 Admin）可设
        var isHq = cu.Role == AttendanceSystem.Models.Enums.UserRole.Admin && cu.ScopedDepartmentId is null;
        if ((targetRole is AttendanceSystem.Models.Enums.UserRole.Admin or AttendanceSystem.Models.Enums.UserRole.Clerk) && !isHq)
            return (false, "管理员/文员角色只有总部超级管理员能设置（受限管理员不能创建无范围文员）");
        // 反过来：目标现在就是管理员的话，把他改成别的角色（等于剥夺管理权限）同样只有总部超级管理员能做——
        // 这条以前完全没查，配合 role 大小写的修复一起补上（不然大小写一修好，文员就能把总部管理员直接
        // 降级成普通员工，2026-09-29 审查发现）。这里没走 UserService.UpdateUserAsync（下面是直接改字段），
        // 所以要在这里单独查一次，不能只指望服务层那道检查
        if (!Middlewares.CurrentUser.CanManageAccountCore(cu.Role, cu.ScopedDepartmentId, target.Role, target.ScopedDepartmentId))
            return (false, "无权调整该账号的角色（角色层级限制）");

        var u = await db.Users.FindAsync(uid.Value);
        u!.Role = targetRole.Value;
        u.UpdatedAt = clock.LocalNow();
        await db.SaveChangesAsync();
        return (true, $"已把 {u.RealName}（{u.EmployeeNo}）的角色调整为 {RoleText(targetRole.Value)}（原角色 {RoleText(target.Role)}）");
    }

    private static string RoleText(AttendanceSystem.Models.Enums.UserRole r) => r switch
    {
        AttendanceSystem.Models.Enums.UserRole.Admin => "管理员",
        AttendanceSystem.Models.Enums.UserRole.Clerk => "文员",
        AttendanceSystem.Models.Enums.UserRole.Supervisor => "主管",
        AttendanceSystem.Models.Enums.UserRole.TeamLeader => "班组长",
        _ => "员工"
    };

    private async Task<(bool, string)> ExecuteBatchToggleAsync(int operatorUserId, JsonElement args)
    {
        var action = GetString(args, "action");
        var ids = IntListArg(args, "userIds");
        if (action is not ("deactivate" or "activate") || ids.Count == 0)
            return (false, "参数不正确：action 与 userIds");
        if (ids.Contains(operatorUserId)) return (false, "列表不能包含你自己");

        var (_, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);

        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DepartmentId, u.IsActive, u.IsBlacklisted })
            .ToListAsync();
        if (users.Count != ids.Count) return (false, "部分目标员工不存在");
        if (users.Any(u => u.IsBlacklisted)) return (false, "列表包含黑名单员工，请单独处理");
        if (visibleIds is not null &&
            users.Any(u => u.DepartmentId is null || !visibleIds.Contains(u.DepartmentId.Value)))
            return (false, "列表包含不在你管理范围内的员工，请修正后重试");

        var targets = action == "deactivate"
            ? users.Where(u => u.IsActive).Select(u => u.Id).ToList()
            : users.Where(u => !u.IsActive).Select(u => u.Id).ToList();
        if (targets.Count == 0) return (false, "这些员工已处于目标状态");

        var n = await userService.SetActiveBatchAsync(targets, action == "activate", operatorUserId);
        return (true, $"已{(action == "deactivate" ? "停用" : "启用")} {n} 名员工");
    }
}
