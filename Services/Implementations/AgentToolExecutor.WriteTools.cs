using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>写工具与高风险提案（只落待确认动作，不直接改业务数据）（<see cref="AgentToolExecutor"/> 的一部分）。</summary>
public partial class AgentToolExecutor
{
    // ── 写工具（提案式：只落 AgentPendingAction，绝不直接改业务数据）─────────────────

    /// <summary>校验会话归属并把一条提案落库（写工具的公共入口）。</summary>
    private async Task<(AgentPendingAction? action, string? error)> CreateProposalAsync(
        int operatorUserId, int conversationId, string toolName, string paramJson, string summary, CancellationToken ct)
    {
        var convOwned = await db.AgentConversations
            .AnyAsync(c => c.Id == conversationId && c.UserId == operatorUserId && c.IsActive, ct);
        if (!convOwned)
            return (null, "错误：会话不存在或不属于你");

        var action = new AgentPendingAction
        {
            ConversationId = conversationId,
            ToolName       = toolName,
            ParamJson      = paramJson,
            SummaryText    = summary.Length > 480 ? summary[..480] : summary,
            Status         = Models.Enums.AgentActionStatus.Pending,
            CreatedBy      = operatorUserId,
            CreatedAt      = clock.LocalNow(),
            ExpiresAt      = clock.LocalNow().AddMinutes(15)
        };
        db.AgentPendingActions.Add(action);
        await db.SaveChangesAsync(ct);
        return (action, null);
    }

    private async Task<string> PunchAdjustProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var userId    = IntArg(args, "userId");
        var workDateS = StrArg(args, "workDate");
        var clockInS  = StrArg(args, "clockIn")?.Trim();
        var clockOutS = StrArg(args, "clockOut")?.Trim();
        var remark    = StrArg(args, "remark")?.Trim();

        if (!userId.HasValue || userId.Value <= 0 || !DateOnly.TryParse(workDateS, out var workDate))
            return "错误：需要有效的 userId 与 workDate（yyyy-MM-dd）";
        if (string.IsNullOrEmpty(clockInS) && string.IsNullOrEmpty(clockOutS))
            return "错误：上班/下班打卡时间至少要填一个（HH:mm）";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (user is null) return "错误：目标员工不存在";
        if (visibleIds is not null && (user.DepartmentId is null || !visibleIds.Contains(user.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        // 补卡跟"停用/启用"等其它高风险操作一样，只查了部门范围、漏了角色层级检查——文员能借此给
        // 总部超管补卡（2026-09-30 复核发现，属于第 12 轮 S1 同一类漏洞漏掉的工具）
        var padjOp = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (padjOp is null || !Middlewares.CurrentUser.CanManageAccountCore(padjOp.Role, padjOp.ScopedDepartmentId, user.Role, user.ScopedDepartmentId))
            return "错误：无权给该账号补卡（角色层级限制）";

        var param = JsonSerializer.Serialize(new { userId = user.Id, workDate = workDate.ToString("yyyy-MM-dd"), clockIn = clockInS, clockOut = clockOutS, remark });
        var summary = $"给 {user.RealName}（{user.EmployeeNo}）补录 {workDate:yyyy-MM-dd}："
                    + $"上班{(string.IsNullOrEmpty(clockInS) ? "--" : clockInS)} / 下班{(string.IsNullOrEmpty(clockOutS) ? "--" : clockOutS)}"
                    + (string.IsNullOrEmpty(remark) ? "" : $"（{remark}）");

        var (action, perr) = await CreateProposalAsync(operatorUserId, conversationId, "punch_adjust_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{action!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」才会真正补卡；15 分钟内有效。";
    }

    private async Task<string> RejectRegistrationProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var registrationId = IntArg(args, "registrationId");
        var reason = StrArg(args, "reason")?.Trim();
        if (!registrationId.HasValue || registrationId.Value <= 0)
            return "错误：需要有效的 registrationId";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var reg = await db.EmployeeRegistrations.AsNoTracking()
            .Where(r => r.Id == registrationId.Value)
            .Select(r => new { r.Id, r.RealName, r.DepartmentId, r.Status })
            .FirstOrDefaultAsync(ct);
        if (reg is null) return "错误：该登记不存在";
        if (reg.Status != RegistrationStatus.Pending) return "错误：该登记已不是待确认状态";
        if (visibleIds is not null && (reg.DepartmentId is null || !visibleIds.Contains(reg.DepartmentId.Value)))
            return "错误：该登记不在你的管理范围内（无部门归属的登记由总部处理）";

        var param = JsonSerializer.Serialize(new { registrationId = reg.Id, reason });
        var summary = $"驳回待确认登记 #{reg.Id}（{reg.RealName}）" + (string.IsNullOrEmpty(reason) ? "" : $"：{reason}");

        var (action, perr) = await CreateProposalAsync(operatorUserId, conversationId, "registration_reject_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{action!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」才会真正驳回；15 分钟内有效。";
    }

    private async Task<string> UserToggleProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var userId = IntArg(args, "userId");
        var action = StrArg(args, "action");
        if (!userId.HasValue || userId.Value <= 0 || action is not ("deactivate" or "activate"))
            return "错误：需要有效的 userId 与 action（deactivate=停用 / activate=启用）";
        if (userId.Value == operatorUserId)
            return "错误：不能对自己发起停用/启用操作，请在员工管理页处理";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.IsActive, u.IsBlacklisted, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (user is null) return "错误：目标员工不存在";
        if (visibleIds is not null && (user.DepartmentId is null || !visibleIds.Contains(user.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        var toggleOp = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (toggleOp is null || !Middlewares.CurrentUser.CanManageAccountCore(toggleOp.Role, toggleOp.ScopedDepartmentId, user.Role, user.ScopedDepartmentId))
            return "错误：无权操作该账号（角色层级限制）";

        if (user.IsBlacklisted) return "错误：黑名单员工请用员工管理页的专门功能";
        if (action == "deactivate" && !user.IsActive) return "错误：该员工已是停用状态";
        if (action == "activate" && user.IsActive) return "错误：该员工本来就在职";

        var param = JsonSerializer.Serialize(new { userId = user.Id, action });
        var summary = $"{(action == "deactivate" ? "停用" : "启用")}员工 {user.RealName}（{user.EmployeeNo}）";

        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "user_toggle_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」才会真正{(action == "deactivate" ? "停用" : "启用")}；15 分钟内有效。";
    }

    // ── 高风险提案（删除/拉黑/重置密码/改管理范围——同样只落提案）────────────────────

    /// <summary>提案级校验：目标员工存在且非本人且在范围内、且操作者管得到这个角色（角色层级限制，
    /// 跟确认执行时 UserService.EnsureCanManageAsync 同一套口径——以前生成待确认动作这一步完全不查，
    /// 文员对总部超管发起重置密码/删除/拉黑照样能生成一张卡片，只是确认时才报错，容易误导管理员
    /// 以为这个操作是被允许的，2026-09-29 第 12 轮审查发现）；返回 (userId, 名字, 错误)。</summary>
    private async Task<(int? userId, string? display, string? error)> ResolveTargetAsync(
        int operatorUserId, HashSet<int>? visibleIds, int? rawUserId)
    {
        if (!rawUserId.HasValue || rawUserId.Value <= 0)
            return (null, null, "错误：需要有效的 userId（先用 user_search 查询）");
        if (rawUserId.Value == operatorUserId)
            return (null, null, "错误：不能对自己执行该操作，请在员工管理页处理");

        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == rawUserId.Value)
            .Select(x => new { x.Id, x.RealName, x.EmployeeNo, x.DepartmentId, x.Role, x.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        if (u is null) return (null, null, "错误：目标员工不存在");
        if (visibleIds is not null && (u.DepartmentId is null || !visibleIds.Contains(u.DepartmentId.Value)))
            return (null, null, "错误：该员工不在你的管理范围内");

        var op = await db.Users.AsNoTracking()
            .Where(x => x.Id == operatorUserId)
            .Select(x => new { x.Role, x.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        if (op is null || !Middlewares.CurrentUser.CanManageAccountCore(op.Role, op.ScopedDepartmentId, u.Role, u.ScopedDepartmentId))
            return (null, null, "错误：无权操作该账号（角色层级限制）");

        return (u.Id, $"{u.RealName}（{u.EmployeeNo}）", null);
    }

    private async Task<string> UserDeleteProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var rawId = IntArg(doc.RootElement, "userId");

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var (userId, display, rerr) = await ResolveTargetAsync(operatorUserId, visibleIds, rawId);
        if (rerr is not null) return rerr;

        var isBlacklisted = await db.Users.Where(u => u.Id == userId!.Value).Select(u => u.IsBlacklisted).FirstOrDefaultAsync(ct);
        if (isBlacklisted) return "错误：黑名单员工请先移出黑名单（保留记录防重复用工）";
        // 有考勤/打卡/申请历史的人只能停用，生成提案阶段就拦下，不生成一张注定执行失败的卡片
        if (await UserDeletionGuard.HasHistoryDataAsync(db, userId!.Value, ct))
            return "错误：" + UserDeletionGuard.BlockedMessage + "（可以用 user_toggle_propose 停用）";

        var param = JsonSerializer.Serialize(new { userId });
        var summary = $"【高风险】彻底删除员工 {display}（该账号没有任何考勤/打卡/申请记录；删除后不可恢复）";
        var (action, perr) = await CreateProposalAsync(operatorUserId, conversationId, "user_delete_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{action!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> UserBlacklistProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var rawId  = IntArg(doc.RootElement, "userId");
        var action = StrArg(doc.RootElement, "action");
        if (action is not ("blacklist" or "remove"))
            return "错误：需要 action（blacklist=拉黑 / remove=移出黑名单）";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var (userId, display, rerr) = await ResolveTargetAsync(operatorUserId, visibleIds, rawId);
        if (rerr is not null) return rerr;

        var state = await db.Users.Where(u => u.Id == userId!.Value)
            .Select(u => new { u.IsActive, u.IsBlacklisted }).FirstOrDefaultAsync(ct);
        if (action == "blacklist" && state!.IsBlacklisted) return "错误：该员工已在黑名单";
        if (action == "remove" && !state!.IsBlacklisted) return "错误：该员工不在黑名单";

        var param = JsonSerializer.Serialize(new { userId, action });
        var verb = action == "blacklist" ? "拉黑" : "移出黑名单";
        var summary = $"【高风险】{verb}员工 {display}（拉黑=禁止登录、工号永不再用且全公司共享）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "user_blacklist_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> PasswordResetProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var rawId = IntArg(doc.RootElement, "userId");

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var (userId, display, rerr) = await ResolveTargetAsync(operatorUserId, visibleIds, rawId);
        if (rerr is not null) return rerr;

        var param = JsonSerializer.Serialize(new { userId });
        var summary = $"【高风险】重置员工 {display} 的登录密码（系统自动生成随机密码，仅确认页面展示一次）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "password_reset_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」后查看一次性新密码；15 分钟内有效。";
    }

    private async Task<string> ScopeChangeProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root  = doc.RootElement;
        var rawId = IntArg(root, "userId");
        var action = StrArg(root, "action");
        var deptId = IntArg(root, "deptId");

        // 仅总部超级管理员（角色 Admin 且自己不受范围限制）
        var op = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        if (op is null || op.Role != AttendanceSystem.Models.Enums.UserRole.Admin || op.ScopedDepartmentId.HasValue)
            return "错误：只有总部超级管理员可以调整管理范围";
        if (action is not ("set" or "clear")) return "错误：需要 action（set=指定范围 / clear=清空范围）";
        if (!rawId.HasValue || rawId.Value <= 0) return "错误：需要有效的 userId";
        if (rawId.Value == operatorUserId) return "错误：不能调整自己的管理范围，请联系另一位总部管理员处理";

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == rawId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo })
            .FirstOrDefaultAsync();
        if (target is null) return "错误：目标账号不存在";

        string? deptName = null;
        int? affectedCount = null;
        if (action == "set")
        {
            if (!deptId.HasValue) return "错误：action=set 时必须提供 deptId";
            deptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync();
            if (deptName is null) return "错误：指定的部门不存在";
            var subtree = await deptScope.GetSubtreeIdsAsync(deptId.Value);
            affectedCount = await db.Users.CountAsync(u => u.DepartmentId != null && subtree.Contains(u.DepartmentId.Value), ct);
        }

        var param = JsonSerializer.Serialize(new { userId = target.Id, action, deptId = action == "set" ? deptId : (int?)null });
        var summary = action == "set"
            ? $"【仅总部·高风险】把 {target.RealName}（{target.EmployeeNo}）设为管理范围【{deptName}】（该部门及下级共 {affectedCount} 名员工将进入其可见/可管范围）"
            : $"【仅总部·高风险】清空 {target.RealName}（{target.EmployeeNo}）的管理范围（恢复不受限，可见/可管全公司）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "scope_change_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」；15 分钟内有效。";
    }

    // 认领登记并建档（提案版）：校验登记/部门/上级都在范围内
    private async Task<string> RegistrationConfirmProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root         = doc.RootElement;
        var registrationId = IntArg(root, "registrationId");
        var deptId       = IntArg(root, "deptId");
        var supervisorId = IntArg(root, "supervisorId");
        var employeeNo   = StrArg(root, "employeeNo")?.Trim();
        if (!registrationId.HasValue || !deptId.HasValue || !supervisorId.HasValue)
            return "错误：需要 registrationId、deptId、supervisorId（先用 pending_registration_list / user_search 查）";
        if (!string.IsNullOrEmpty(employeeNo) &&
            !System.Text.RegularExpressions.Regex.IsMatch(employeeNo, @"^[A-Za-z0-9_-]{1,50}$"))
            return "错误：工号只能包含字母/数字/下划线/短横线";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var reg = await db.EmployeeRegistrations.AsNoTracking()
            .Where(r => r.Id == registrationId.Value)
            .Select(r => new { r.Id, r.RealName, r.DepartmentId, r.Status })
            .FirstOrDefaultAsync(ct);
        if (reg is null) return "错误：该登记不存在";
        if (reg.Status != RegistrationStatus.Pending) return "错误：该登记已不是待确认状态";
        // 无部门归属的登记只有总部能建档（部门由总部指定）
        if (visibleIds is not null && (reg.DepartmentId is null || !visibleIds.Contains(reg.DepartmentId.Value)))
            return "错误：该登记不在你的管理范围内";
        if (visibleIds is not null && !visibleIds.Contains(deptId.Value))
            return "错误：归属部门不在你的管理范围内";

        var deptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct);
        if (deptName is null) return "错误：归属部门不存在";
        if (supervisorId.Value == operatorUserId)
            return "错误：直属上级不能是自己，请选该部门的主管";
        var sup = await db.Users.AsNoTracking()
            .Where(u => u.Id == supervisorId.Value)
            .Select(u => new { u.IsActive, u.Role, u.DepartmentId, u.RealName })
            .FirstOrDefaultAsync(ct);
        if (sup is null || !sup.IsActive || sup.Role != UserRole.Supervisor)
            return "错误：直属上级必须是在职的主管（角色=主管）";
        if (sup.DepartmentId != deptId.Value)
            return "错误：直属上级必须和员工在同一个部门";

        var param = JsonSerializer.Serialize(new { registrationId = reg.Id, deptId = deptId.Value, supervisorId = supervisorId.Value, employeeNo });
        var summary = $"把待确认登记 #{reg.Id}（{reg.RealName}）建档为【{deptName}】员工"
                    + (string.IsNullOrEmpty(employeeNo) ? "（工号自动生成）" : $"（工号 {employeeNo}）")
                    + "，初始密码 123456";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "registration_confirm_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」才会真正建号；15 分钟内有效。";
    }


    // 角色调整（提案）：employee/clerk/supervisor/teamleader/admin
    private async Task<string> EmployeeRoleProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root  = doc.RootElement;
        var uid   = IntArg(root, "userId");
        var roleS = StrArg(root, "role");
        UserRole? newRole = roleS switch
        {
            "employee"   => UserRole.Employee,
            "clerk"      => UserRole.Clerk,
            "supervisor" => UserRole.Supervisor,
            "teamleader" => UserRole.TeamLeader,
            "admin"      => UserRole.Admin,
            _            => null
        };
        if (!uid.HasValue || uid.Value <= 0) return "错误：需要 userId";
        if (!newRole.HasValue) return "错误：role 应为 employee/clerk/supervisor/teamleader/admin";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        if (uid.Value == operatorUserId) return "错误：不能调整自己的角色，请联系总部管理员";

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == uid.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (target is null) return "错误：目标员工不存在";
        if (visibleIds is not null && (target.DepartmentId is null || !visibleIds.Contains(target.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        var op = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        var isHq = op is { Role: UserRole.Admin } && op.ScopedDepartmentId is null;
        // 权限闸：admin/clerk 只有总部（不受限且 Admin 角色）能设
        if ((newRole is UserRole.Admin or UserRole.Clerk) && !isHq)
            return "错误：管理员/文员角色只有总部超级管理员能设置（受限管理员不能创建无范围文员）";
        // 反过来也要挡：目标现在就是管理员的话，改成别的角色（等于剥夺他的管理权限）同样只有总部超级管理员能做，
        // 跟 CanManageAccount 同一套角色层级（2026-09-29 审查发现，配合下面的 role 大小写修复一起改，
        // 不然大小写一修好，文员就能把总部管理员直接降级成普通员工）
        if (op is null || !Middlewares.CurrentUser.CanManageAccountCore(op.Role, op.ScopedDepartmentId, target.Role, target.ScopedDepartmentId))
            return "错误：无权调整该账号的角色（角色层级限制）";

        var param = JsonSerializer.Serialize(new { userId = target.Id, role = newRole.Value.ToString().ToLowerInvariant() });
        var summary = $"调整员工 {target.RealName}（{target.EmployeeNo}）角色：{RoleText(target.Role)} → {RoleText(newRole.Value)}";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "employee_role_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }
}
