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

/// <summary>执行分发与基础动作（驳回登记、停启用、补卡）（<see cref="AgentActionService"/> 的一部分）。</summary>
public partial class AgentActionService
{
    // ── 执行分发（每类动作的真实业务；均先重新做范围/状态校验）──────────────────

    private async Task<(bool ok, string message)> ExecuteApprovedAsync(AgentPendingAction action, int operatorUserId)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(action.ParamJson) ? "{}" : action.ParamJson);
            var args = doc.RootElement;

            return action.ToolName switch
            {
                "registration_reject_propose" => await ExecuteRejectRegistrationAsync(operatorUserId, args),
                "user_toggle_propose"         => await ExecuteToggleUserAsync(operatorUserId, args),
                "punch_adjust_propose"        => await ExecutePunchAdjustAsync(operatorUserId, args),
                "user_delete_propose"         => await ExecuteDeleteUserAsync(operatorUserId, args),
                "user_blacklist_propose"      => await ExecuteBlacklistUserAsync(operatorUserId, args),
                "password_reset_propose"      => await ExecutePasswordResetAsync(operatorUserId, args),
                "scope_change_propose"        => await ExecuteScopeChangeAsync(operatorUserId, args),
                "registration_confirm_propose" => await ExecuteConfirmRegistrationAsync(operatorUserId, args),
                "employee_create_propose"      => await ExecuteCreateEmployeeAsync(operatorUserId, args),
                "employee_update_propose"      => await ExecuteUpdateEmployeeAsync(operatorUserId, args),
                "employee_role_propose"        => await ExecuteChangeRoleAsync(operatorUserId, args),
                "employee_batch_toggle_propose" => await ExecuteBatchToggleAsync(operatorUserId, args),
                "approval_handle_propose"      => await ExecuteHandleApprovalAsync(operatorUserId, args),
                "approval_submit_on_behalf_propose" => await ExecuteApprovalSubmitOnBehalfAsync(operatorUserId, args),
                "announcement_publish_propose" => await ExecutePublishAnnouncementAsync(operatorUserId, args),
                "announcement_withdraw_propose" => await ExecuteWithdrawAnnouncementAsync(operatorUserId, args),
                "device_register_propose"      => await ExecuteRegisterDeviceAsync(operatorUserId, args),
                "device_update_propose"        => await ExecuteUpdateDeviceAsync(operatorUserId, args),
                _                             => (false, $"未知动作类型 {action.ToolName}")
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AGENT 动作执行失败：{ToolName} 动作 {ActionId}", action.ToolName, action.Id);
            return (false, AgentErrorText.ForUser(ex));
        }
    }

    /// <summary>从 DB 读操作者并构造范围上下文（每次执行都重读，不信任缓存）。</summary>
    private async Task<(CurrentUser cu, HashSet<int>? visibleIds, bool ok, string? err)> LoadOperatorAsync(int userId)
    {
        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.IsActive, x.Role, x.ScopedDepartmentId, x.RealName })
            .FirstOrDefaultAsync();
        if (u is null || !u.IsActive)
            return (null!, null, false, "操作者账号不存在或已停用");

        var cu = new CurrentUser
        {
            UserId             = userId,
            Role               = u.Role,
            RealName           = u.RealName,
            ScopedDepartmentId = u.ScopedDepartmentId
        };
        var visibleIds = await deptScope.GetVisibleDeptIdsAsync(cu);
        return (cu, visibleIds, true, null);
    }

    // 驳回待确认登记：范围（登记 DepartmentId ∈ 操作者可见；null=仅总部）+ 状态（Pending）双校验
    private async Task<(bool, string)> ExecuteRejectRegistrationAsync(int operatorUserId, JsonElement args)
    {
        var registrationId = GetInt(args, "registrationId");
        if (!registrationId.HasValue) return (false, "参数缺失：registrationId");
        var reason = GetString(args, "reason");

        var (cu, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);

        var reg = await db.EmployeeRegistrations.AsNoTracking()
            .Where(r => r.Id == registrationId.Value)
            .Select(r => new { r.Id, r.RealName, r.DepartmentId, r.Status })
            .FirstOrDefaultAsync();
        if (reg is null) return (false, "该登记不存在");
        if (reg.Status != RegistrationStatus.Pending) return (false, $"该登记已不是待确认状态（当前：{reg.Status}）");
        if (visibleIds is not null && (reg.DepartmentId is null || !visibleIds.Contains(reg.DepartmentId.Value)))
            return (false, "该登记不在你的管理范围内，无权驳回");

        await registrationService.RejectAsync(reg.Id, reason, cu);
        return (true, $"已驳回待确认登记 #{reg.Id}（{reg.RealName}）");
    }

    // 启用/停用员工：范围 + 本人防护 + 目标状态
    private async Task<(bool, string)> ExecuteToggleUserAsync(int operatorUserId, JsonElement args)
    {
        var targetId = GetInt(args, "userId");
        var action   = GetString(args, "action");
        if (!targetId.HasValue || action is not ("deactivate" or "activate"))
            return (false, "参数不正确：需要 userId 与 action(deactivate/activate)");

        var (_, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);

        if (targetId.Value == operatorUserId)
            return (false, "不能对自己执行启用/停用，请在员工管理页操作");

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == targetId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.IsActive, u.IsBlacklisted })
            .FirstOrDefaultAsync();
        if (user is null) return (false, "目标员工不存在");
        if (visibleIds is not null && (user.DepartmentId is null || !visibleIds.Contains(user.DepartmentId.Value)))
            return (false, "该员工不在你的管理范围内，无权操作");
        if (user.IsBlacklisted) return (false, "黑名单员工请用员工管理页的专门功能处理");

        if (action == "deactivate")
        {
            if (!user.IsActive) return (false, "该员工已是停用状态");
            await userService.DeactivateUserAsync(user.Id, operatorUserId);
            return (true, $"已停用 {user.RealName}（{user.EmployeeNo}）");
        }
        else
        {
            if (user.IsActive) return (false, "该员工本来就在职");
            await userService.ActivateUserAsync(user.Id, operatorUserId);
            return (true, $"已启用 {user.RealName}（{user.EmployeeNo}）");
        }
    }

    // 管理员补卡：与 PunchAdjust 页同口径（范围校验 + 至少一个时间），复用 AdminAdjustPunchAsync
    private async Task<(bool, string)> ExecutePunchAdjustAsync(int operatorUserId, JsonElement args)
    {
        var userId    = GetInt(args, "userId");
        var workDateS = GetString(args, "workDate");
        if (!userId.HasValue || !DateOnly.TryParse(workDateS, out var workDate))
            return (false, "参数不正确：需要 userId 与 workDate(yyyy-MM-dd)");

        var (cu, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        if (user is null) return (false, "目标员工不存在");
        if (!await deptScope.CanAccessDeptAsync(cu, user.DepartmentId))
            return (false, "该员工不在你的管理范围内，无权补卡");
        // 提案阶段已经查过一次，这里是确认执行阶段的权威兜底——跟其它高风险操作（停用/删除/改范围）同一道检查
        // （2026-09-30 复核发现：这里之前只查了部门范围，漏了角色层级，文员能给总部超管补卡）
        if (!Middlewares.CurrentUser.CanManageAccountCore(cu.Role, cu.ScopedDepartmentId, user.Role, user.ScopedDepartmentId))
            return (false, "无权给该账号补卡（角色层级限制）");

        var clockInS  = GetString(args, "clockIn");
        var clockOutS = GetString(args, "clockOut");
        var remark    = GetString(args, "remark");

        var clockIn  = ParseTime(clockInS, workDate);
        var clockOut = ParseTime(clockOutS, workDate);
        if (clockIn is null && clockOut is null)
            return (false, "上班/下班打卡时间至少要填一个");

        // 下班卡按补卡审批同一套顺延规则算：ParseTime 只会把时间拼到 workDate 当天，夜班下班卡填的是
        // "第二天早上几点"会被错误地拼在当天，变成下班早于上班的"时间倒挂"（2026-09-30 复核发现：
        // 助手回复"已补录"，实际记录工时算成 0、备注写着"打卡时间异常，需人工核实"）
        if (clockOut is not null && TimeOnly.TryParse(clockOutS!.Trim(), out var outTime))
        {
            var existingIn = clockIn ?? await db.AttendanceRecords
                .Where(r => r.UserId == user.Id && r.WorkDate == workDate).Select(r => r.ClockInTime).FirstOrDefaultAsync();
            var punchShift = (await attendanceService.GetShiftAssignmentAsync(user.Id, workDate))?.ShiftSchedule;
            clockOut = AttendanceService.ResolvePunchReplenishmentClockOut(workDate, outTime, existingIn, punchShift);
        }

        await attendanceService.AdminAdjustPunchAsync(
            user.Id, workDate, clockIn, clockOut, remark, cu.RealName ?? $"管理员{operatorUserId}");
        return (true, $"已为 {user.RealName}（{user.EmployeeNo}）补录 {workDate:yyyy-MM-dd} 的打卡记录");
    }

    private static int? GetInt(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static string? GetString(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>把 "HH:mm" 拼到指定日期上；解析不了返回 null（视为没填）。跨天班次的深夜打卡请走补卡页面处理。</summary>
    private static DateTime? ParseTime(string? hhmm, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(hhmm)) return null;
        if (!TimeOnly.TryParse(hhmm.Trim(), out var t)) return null;
        return date.ToDateTime(t);
    }
}
