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

/// <summary>高风险动作的执行（删除、拉黑、重置密码、改管理范围、确认登记）（<see cref="AgentActionService"/> 的一部分）。</summary>
public partial class AgentActionService
{
    // ── 高风险执行器（删除/拉黑/重置密码/改管理范围）────────────────────────────

    /// <summary>公共守卫：目标员工存在、非本人、且在操作者管理范围内。</summary>
    private async Task<(Models.Entities.User? user, bool ok, string? err)> LoadTargetInScopeAsync(
        int operatorUserId, HashSet<int>? visibleIds, int targetId)
    {
        if (targetId == operatorUserId)
            return (null, false, "不能对自己执行该操作，请在员工管理页处理");

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == targetId)
            .FirstOrDefaultAsync();
        if (user is null) return (null, false, "目标员工不存在");
        if (visibleIds is not null && (user.DepartmentId is null || !visibleIds.Contains(user.DepartmentId.Value)))
            return (null, false, "该员工不在你的管理范围内，无权操作");
        return (user, true, null);
    }

    private async Task<(bool, string)> ExecuteDeleteUserAsync(int operatorUserId, JsonElement args)
    {
        var targetId = GetInt(args, "userId");
        if (!targetId.HasValue) return (false, "参数缺失：userId");

        var (_, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);
        var (user, ok2, err2) = await LoadTargetInScopeAsync(operatorUserId, visibleIds, targetId.Value);
        if (!ok2) return (false, err2!);
        if (user!.IsBlacklisted) return (false, "黑名单员工请先移出黑名单再删除（保留黑名单记录防重复用工）");

        await userService.DeleteUserAsync(user.Id, operatorUserId);
        return (true, $"已彻底删除员工 {user.RealName}（{user.EmployeeNo}）。该操作不可恢复（该账号没有考勤/打卡/申请记录）。");
    }

    private async Task<(bool, string)> ExecuteBlacklistUserAsync(int operatorUserId, JsonElement args)
    {
        var targetId = GetInt(args, "userId");
        var action   = GetString(args, "action");
        if (!targetId.HasValue || action is not ("blacklist" or "remove"))
            return (false, "参数不正确：需要 userId 与 action(blacklist/remove)");

        var (_, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);
        var (user, ok2, err2) = await LoadTargetInScopeAsync(operatorUserId, visibleIds, targetId.Value);
        if (!ok2) return (false, err2!);

        if (action == "blacklist")
        {
            if (user!.IsBlacklisted) return (false, "该员工已在黑名单");
            await userService.BlacklistUserAsync(user.Id, operatorUserId);
            return (true, $"已将 {user.RealName}（{user.EmployeeNo}）拉黑：禁止登录、工号永不再用（黑名单全公司共享）。");
        }
        else
        {
            if (!user!.IsBlacklisted) return (false, "该员工不在黑名单");
            await userService.RemoveFromBlacklistAsync(user.Id, operatorUserId);
            return (true, $"已把 {user.RealName}（{user.EmployeeNo}）移出黑名单（当前为停用状态，需要可再启用）。");
        }
    }

    private async Task<(bool, string)> ExecutePasswordResetAsync(int operatorUserId, JsonElement args)
    {
        var targetId = GetInt(args, "userId");
        if (!targetId.HasValue) return (false, "参数缺失：userId");

        var (_, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);
        var (target, ok2, err2) = await LoadTargetInScopeAsync(operatorUserId, visibleIds, targetId.Value);
        if (!ok2) return (false, err2!);
        var u = target!;

        var pwd = await userService.ResetPasswordAsync(u.Id, operatorUserId);   // newPassword 不传=随机生成
        return (true, $"已重置 {u.RealName}（{u.EmployeeNo}）的登录密码。新密码：{pwd}\n（新密码只在本次页面上显示这一次，请立即转告本人；系统不会强制首次登录改密，建议提醒本人自行改一次）");
    }

    private async Task<(bool, string)> ExecuteScopeChangeAsync(int operatorUserId, JsonElement args)
    {
        var targetId = GetInt(args, "userId");
        var action   = GetString(args, "action");
        if (!targetId.HasValue || action is not ("set" or "clear"))
            return (false, "参数不正确：需要 userId 与 action(set=指定范围 / clear=清空范围)");

        var (cu, _, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);
        // 只有总部超级管理员（角色 Admin 且自己不受范围限制）能给别人设置/清空管理范围
        if (cu.Role != AttendanceSystem.Models.Enums.UserRole.Admin || cu.ScopedDepartmentId.HasValue)
            return (false, "只有总部超级管理员可以调整管理范围");

        if (targetId.Value == operatorUserId)
            return (false, "不能调整自己的管理范围，请联系另一位总部管理员处理");

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == targetId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo })
            .FirstOrDefaultAsync();
        if (target is null) return (false, "目标账号不存在");

        int? newDeptId = null;
        string deptName = "";
        if (action == "set")
        {
            newDeptId = GetInt(args, "deptId");
            if (!newDeptId.HasValue) return (false, "设置范围时必须提供 deptId");
            var dept = await db.Departments.AsNoTracking()
                .Where(d => d.Id == newDeptId.Value)
                .Select(d => d.DeptName)
                .FirstOrDefaultAsync();
            if (dept is null) return (false, "指定的部门不存在");
            deptName = dept;
        }

        await userService.SetScopedDepartmentAsync(target.Id, newDeptId);
        return action == "set"
            ? (true, $"已把 {target.RealName}（{target.EmployeeNo}）设为管理范围【{deptName}】（含其下级部门）")
            : (true, $"已清空 {target.RealName}（{target.EmployeeNo}）的管理范围——该账号现在不受限（若角色为管理员即为总部级）");
    }

    /// <summary>
    /// 认领一条待确认登记并正式建档（镜像"员工管理→待确认→确认录入"页面逻辑）：
    /// 校验登记与目标部门/上级在范围内 → 原子认领登记 → 建员工（初始密码 123456，系统不会强制改密，需要提醒本人自行修改）→ 登记标记已确认。
    /// 注意：认领动作是幂等抢锁；若建档失败登记会被消费掉（与页面行为一致，需人工善后）。
    /// </summary>
    private async Task<(bool, string)> ExecuteConfirmRegistrationAsync(int operatorUserId, JsonElement args)
    {
        var registrationId = GetInt(args, "registrationId");
        var deptId         = GetInt(args, "deptId");
        var supervisorId   = GetInt(args, "supervisorId");
        var employeeNo     = GetString(args, "employeeNo")?.Trim();
        if (!registrationId.HasValue || !deptId.HasValue || !supervisorId.HasValue)
            return (false, "参数不完整：registrationId/deptId/supervisorId");
        if (!string.IsNullOrEmpty(employeeNo) &&
            !System.Text.RegularExpressions.Regex.IsMatch(employeeNo, @"^[A-Za-z0-9_-]{1,50}$"))
            return (false, "工号只能包含字母/数字/下划线/短横线");

        var (cu, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);
        if (supervisorId.Value == operatorUserId) return (false, "直属上级不能是自己");

        var reg = await db.EmployeeRegistrations.AsNoTracking()
            .Where(r => r.Id == registrationId.Value)
            .FirstOrDefaultAsync();
        if (reg is null) return (false, "该登记不存在");
        if (reg.Status != RegistrationStatus.Pending) return (false, "该登记已不是待确认状态");
        if (visibleIds is not null && (reg.DepartmentId is null || !visibleIds.Contains(reg.DepartmentId.Value)))
            return (false, "该登记不在你的管理范围内");
        if (visibleIds is not null && !visibleIds.Contains(deptId.Value))
            return (false, "归属部门不在你的管理范围内");

        var deptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync();
        if (deptName is null) return (false, "归属部门不存在");

        var sup = await db.Users.AsNoTracking()
            .Where(u => u.Id == supervisorId.Value)
            .Select(u => new { u.IsActive, u.Role, u.DepartmentId, u.RealName })
            .FirstOrDefaultAsync();
        if (sup is null || !sup.IsActive || sup.Role != UserRole.Supervisor)
            return (false, "直属上级必须是在职的主管（角色=主管）");
        if (sup.DepartmentId != deptId.Value) return (false, "直属上级必须和员工在同一个部门");

        // 工号：手动指定（查重）或按部门自动生成
        string finalEmployeeNo;
        if (!string.IsNullOrEmpty(employeeNo))
        {
            if (await userService.IsEmployeeNoExistsAsync(employeeNo))
                return (false, $"工号 {employeeNo} 已被占用，请换一个或让系统自动生成");
            finalEmployeeNo = employeeNo;
        }
        else
        {
            finalEmployeeNo = await userService.GenerateNextEmployeeNoAsync(deptId.Value)
                              ?? throw new BusinessException("该部门无法自动生成工号，请手动填写");
        }

        // 部门长期跟随考勤组 → 自动归组（与页面同口径）
        var followedGroupId = await groupService.GetGroupIdForDepartmentAsync(deptId.Value);

        // 原子认领：登记还是 Pending 才能抢到（防两个管理员同时确认同一条）
        if (!await registrationService.ClaimForConfirmAsync(reg.Id, cu))
            return (false, "该登记刚刚已被其他人处理，请刷新列表");

        try
        {
            var newUser = new Models.Entities.User
            {
                EmployeeNo            = finalEmployeeNo,
                RealName              = reg.RealName,
                Role                  = UserRole.Employee,
                DepartmentId          = deptId.Value,
                AttendanceGroupId     = followedGroupId,
                SupervisorUserId      = supervisorId.Value,
                Position              = reg.Position,
                ContractCompany       = reg.ContractCompany,
                Phone                 = reg.Phone,
                IdNumber              = reg.IdNumber?.ToUpperInvariant(),
                HomeAddress           = reg.HomeAddress,
                EmergencyContactName  = reg.EmergencyContactName,
                EmergencyContactPhone = reg.EmergencyContactPhone,
                IdCardPhotoUrl        = reg.IdCardPhotoUrl,   // 沿用登记时上传的照片
                AllowRemotePunch      = false,
                IsActive              = true,
                CreatedAt             = clock.LocalNow(),
                UpdatedAt             = clock.LocalNow()
            };
            await userService.CreateUserAsync(newUser, "123456");   // 固定初始密码（系统不强制首登改密，见下面的提示文案）
            await registrationService.MarkConfirmedAsync(reg.Id, newUser.Id);
            return (true, $"已将登记 #{reg.Id}（{reg.RealName}）建档：工号 {finalEmployeeNo}，部门【{deptName}】，直属上级 {sup.RealName}。初始密码 123456（系统不会强制改密，请提醒本人自行修改）");
        }
        catch (Exception ex)
        {
            // 建档失败：登记已被认领消费（与页面行为一致），提示管理员在员工管理页善后
            logger.LogWarning(ex, "AGENT 认领建档失败：登记 #{RegId}（目标工号 {Eno}）", reg.Id, finalEmployeeNo);
            return (false, $"建档失败：{AgentErrorText.ForUser(ex)}（该登记已被认领，请到\"员工管理→待确认\"核对是否需要人工补建）");
        }
    }
}
