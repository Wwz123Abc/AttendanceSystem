using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>执行入口、范围加载、参数解析等通用辅助（<see cref="AgentToolExecutor"/> 的一部分）。</summary>
public partial class AgentToolExecutor
{
    // ── 执行入口 ─────────────────────────────────────────────────────────────

    public async Task<string> ExecuteAsync(int operatorUserId, int conversationId, string toolName, string argsJson, CancellationToken ct)
    {
        try
        {
            return toolName switch
            {
                "user_search"                 => await UserSearchAsync(operatorUserId, argsJson, ct),
                "pending_registration_list"   => await PendingRegistrationListAsync(operatorUserId, argsJson, ct),
                "attendance_anomaly_list"     => await AttendanceAnomalyListAsync(operatorUserId, argsJson, ct),
                "monthly_summary_get"         => await MonthlySummaryGetAsync(operatorUserId, argsJson, ct),
                "device_status_list"          => await DeviceStatusListAsync(operatorUserId, argsJson, ct),
                "department_list"              => await DepartmentListAsync(operatorUserId, argsJson, ct),
                "attendance_group_list"        => await AttendanceGroupListAsync(operatorUserId, ct),
                "punch_adjust_propose"        => await PunchAdjustProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "registration_reject_propose" => await RejectRegistrationProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "user_toggle_propose"         => await UserToggleProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "user_delete_propose"         => await UserDeleteProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "user_blacklist_propose"      => await UserBlacklistProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "password_reset_propose"      => await PasswordResetProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "scope_change_propose"        => await ScopeChangeProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "registration_confirm_propose" => await RegistrationConfirmProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "employee_create_propose"     => await EmployeeCreateProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "employee_update_propose"     => await EmployeeUpdateProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "employee_role_propose"       => await EmployeeRoleProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "employee_batch_toggle_propose" => await EmployeeBatchToggleProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "approval_pending_list"       => await ApprovalPendingListAsync(operatorUserId, argsJson, ct),
                "approval_handle_propose"     => await ApprovalHandleProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "approval_submit_on_behalf_propose" => await ApprovalSubmitOnBehalfProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "announcement_publish_propose" => await AnnouncementPublishProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "announcement_withdraw_propose" => await AnnouncementWithdrawProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "device_register_propose"     => await DeviceRegisterProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "device_update_propose"       => await DeviceUpdateProposeAsync(operatorUserId, conversationId, argsJson, ct),
                _ => $"错误：未知工具 {toolName}"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AGENT 工具 {Tool} 执行异常（操作者 {UserId}）", toolName, operatorUserId);
            return $"错误：工具执行失败：{AgentErrorText.ForUser(ex)}";
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetQuickBriefAsync(int operatorUserId, CancellationToken ct)
    {
        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return "";

        var today = DateOnly.FromDateTime(clock.LocalNow());
        var anomalyStatuses = new[] { AttendanceStatus.Late, AttendanceStatus.EarlyLeave, AttendanceStatus.Absent, AttendanceStatus.NotPunched };
        var anomalyQ = db.AttendanceRecords.AsNoTracking()
            .Where(r => r.WorkDate == today && anomalyStatuses.Contains(r.AttendanceStatus)
                        && r.User.Role == UserRole.Employee && !r.User.IsAttendanceExempt);   // 免考勤的正式工不算异常
        if (visibleIds is not null)
            anomalyQ = anomalyQ.Where(r => r.User.DepartmentId != null && visibleIds.Contains(r.User.DepartmentId.Value));
        var anomalyCount = await anomalyQ.CountAsync(ct);

        var approvalCount = await db.ApprovalSteps.AsNoTracking()
            .CountAsync(s => s.ApproverUserId == operatorUserId && s.ApprovalStatus == ApprovalStatus.Pending, ct);

        var regQ = db.EmployeeRegistrations.AsNoTracking().Where(r => r.Status == RegistrationStatus.Pending);
        if (visibleIds is not null)
            regQ = regQ.Where(r => r.DepartmentId != null && visibleIds.Contains(r.DepartmentId.Value));
        var regCount = await regQ.CountAsync(ct);

        if (anomalyCount == 0 && approvalCount == 0 && regCount == 0) return "";

        var parts = new System.Collections.Generic.List<string>();
        if (anomalyCount > 0) parts.Add($"今日考勤异常 {anomalyCount} 条");
        if (approvalCount > 0) parts.Add($"待你审批 {approvalCount} 条");
        if (regCount > 0) parts.Add($"待确认登记 {regCount} 条");
        return "顺便先给你播报一下：" + string.Join("，", parts) + "。需要看详情可以直接问我，比如\"查一下今天的异常\"。";
    }

    // ── 通用辅助 ─────────────────────────────────────────────────────────────

    /// <summary>加载操作者并解析其可见部门集合（null=不受限，不过滤）。账号无效返回 (null, 错误文本)。</summary>
    private async Task<(HashSet<int>? visibleIds, string? error)> LoadScopeAsync(int operatorUserId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == operatorUserId)
            .Select(x => new { x.IsActive, x.Role, x.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (u is null || !u.IsActive)
            return (null, "错误：操作者账号不存在或已停用");

        var cu = new CurrentUser
        {
            UserId            = operatorUserId,
            Role              = u.Role,
            ScopedDepartmentId = u.ScopedDepartmentId
        };
        var visibleIds = await deptScope.GetVisibleDeptIdsAsync(cu);
        return (visibleIds, null);
    }

    /// <summary>手机号打码：138****1234；不合法/为空原样返回。</summary>
    private static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "-";
        var p = phone.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(p, @"^1\d{10}$")
            ? p[..3] + "****" + p[^4..]
            : p;
    }

    private static string RoleText(UserRole role) => role switch
    {
        UserRole.Admin      => "管理员",
        UserRole.Clerk      => "文员",
        UserRole.Supervisor => "主管",
        UserRole.TeamLeader => "班组长",
        _                   => "员工"
    };

    private static string UserStateText(User u) =>
        u.IsBlacklisted ? "黑名单" : u.IsActive ? "在职" : "停用";

    private static string AttStatusText(AttendanceStatus s) => s switch
    {
        AttendanceStatus.Late        => "迟到",
        AttendanceStatus.EarlyLeave  => "早退",
        AttendanceStatus.Absent      => "旷工",
        AttendanceStatus.NotPunched  => "缺卡",
        AttendanceStatus.Normal      => "正常",
        AttendanceStatus.Holiday     => "休假",
        AttendanceStatus.OnLeave     => "请假",
        AttendanceStatus.Overtime    => "加班",
        AttendanceStatus.BusinessTrip=> "出差",
        _                            => s.ToString()
    };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…（结果过长已截断，请缩小查询范围）";

    private static string? StrArg(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? IntArg(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool? BoolArg(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>按 deptId（含子树）进一步收窄；deptId 为空时返回 null=不再收窄。</summary>
    private async Task<HashSet<int>?> ResolveSubtreeAsync(HashSet<int>? visibleIds, int? deptId, CancellationToken ct)
    {
        if (!deptId.HasValue) return null;
        // 受限管理员只能看范围内部门：请求的部门若不在可见集合内直接拒绝（返回空集=查不到任何数据）
        if (visibleIds is not null && !visibleIds.Contains(deptId.Value))
            return new HashSet<int>();
        return await deptScope.GetSubtreeIdsAsync(deptId.Value);
    }
}
