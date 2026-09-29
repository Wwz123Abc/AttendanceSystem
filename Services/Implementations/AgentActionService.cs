using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <inheritdoc cref="IAgentActionService"/>
/// <remarks>
/// 实现要点：
/// - 认领/改状态全部用 ExecuteUpdateAsync 在数据库层原子完成（WHERE 带 Status=Pending/未过期），
///   双击或并发只会成功一次；本服务不对已认领的实体再走 SaveChanges 整体写回，避免脏覆盖；
/// - 确认后每个动作都重新做范围/状态校验，校验不过就记失败原因，不硬执行；
/// - 失败不自动回退 Pending（避免重复执行），管理员看到原因后可重新发起。
/// </remarks>
public class AgentActionService(
    AttendanceDbContext db,
    IDeptScopeService deptScope,
    IUserService userService,
    IAttendanceService attendanceService,
    IEmployeeRegistrationService registrationService,
    IAttendanceGroupService groupService,
    IApprovalService approvalService,
    IAnnouncementService announcementService,
    ILogger<AgentActionService> logger) : IAgentActionService
{
    // ── 查询 ────────────────────────────────────────────────────────────────

    public async Task<List<AgentPendingActionDto>> GetActionsAsync(int userId, int conversationId)
    {
        // 顺带把属于该用户的过期提案自动作废
        await ExpireOverdueAsync(userId, conversationId);

        var rows = await db.AgentPendingActions
            .Where(a => a.ConversationId == conversationId && a.CreatedBy == userId)
            .OrderByDescending(a => a.Id)
            .Take(20)
            .Select(a => new
            {
                a.Id, a.ToolName, a.SummaryText, a.Status, a.ErrorText, a.ResultText, a.CreatedAt,
                a.Undoable, a.UndoneAt
            })
            .ToListAsync();

        return rows.Select(a => new AgentPendingActionDto
        {
            Id            = a.Id,
            ToolNameText  = ToolDisplayName(a.ToolName),
            SummaryText   = a.SummaryText,
            StatusText    = a.Status switch
            {
                AgentActionStatus.Pending  => "待确认",
                AgentActionStatus.Approved => a.UndoneAt.HasValue ? "已撤回" : a.ErrorText != null ? "执行失败" : "已执行",
                AgentActionStatus.Rejected => "已拒绝",
                _                          => "已过期"
            },
            CreatedAtText = a.CreatedAt.ToString("MM-dd HH:mm"),
            ResultText    = a.ResultText,
            ErrorText     = a.ErrorText,
            IsPending     = a.Status == AgentActionStatus.Pending,
            HighRisk      = HighRiskTools.Contains(a.ToolName),
            Undoable      = a.Undoable,
            CanUndo       = a.Undoable && a.Status == AgentActionStatus.Approved && a.ErrorText is null && !a.UndoneAt.HasValue,
            UndoHint      = UndoHintOf(a.ToolName)
        }).ToList();
    }

    /// <summary>可撤回的动作类型；删除员工/重置密码不可逆，不在此列。</summary>
    private static readonly HashSet<string> UndoableTools =
    [
        "user_toggle_propose",
        "employee_batch_toggle_propose",
        "employee_update_propose",
        "employee_role_propose",
        "registration_reject_propose",
        "registration_confirm_propose",
        "employee_create_propose",
        "punch_adjust_propose",
        "holiday_add_propose",
        "holiday_delete_propose",
        "user_blacklist_propose",
        "scope_change_propose",
        "approval_submit_on_behalf_propose"
    ];

    /// <summary>撤回会影响的提示文案（确认弹窗用）。</summary>
    private static string UndoHintOf(string toolName) => toolName switch
    {
        "user_toggle_propose"           => "撤回=把账号状态改回执行前（停用→启用 / 启用→停用）",
        "employee_batch_toggle_propose" => "撤回=把这一批账号状态全部改回执行前",
        "employee_update_propose"       => "撤回=恢复被改动的字段为执行前值",
        "employee_role_propose"         => "撤回=把角色改回执行前（如 主管→员工）",
        "registration_reject_propose"   => "撤回=把该登记恢复为待确认（若有重复登记会拒绝撤回）",
        "registration_confirm_propose"  => "撤回=删除刚建的员工并恢复该登记为待确认（其考勤/关联数据会一并删除，请谨慎）",
        "employee_create_propose"       => "撤回=删除刚建的员工（其考勤/关联数据会一并删除，请谨慎）",
        "punch_adjust_propose"          => "撤回=恢复补卡前的打卡记录（原没有记录的会删除该天记录）",
        "holiday_add_propose"           => "撤回=删除这条假期",
        "holiday_delete_propose"        => "撤回=恢复这条假期",
        "user_blacklist_propose"        => "撤回=恢复该员工拉黑前的状态",
        "scope_change_propose"          => "撤回=恢复执行前的管理范围",
        "approval_submit_on_behalf_propose" => "撤回=撤销这张申请单（仅在审批人还没处理、状态仍为「待审批」时可撤）",
        _                               => ""
    };

    /// <summary>高风险工具清单：删除/拉黑/重置密码/调整管理范围——这些动作在页面上要红字提醒。</summary>
    private static readonly HashSet<string> HighRiskTools =
    [
        "user_delete_propose",
        "user_blacklist_propose",
        "password_reset_propose",
        "scope_change_propose"
    ];

    /// <summary>工具名的中文显示名——待确认动作列表、动作审计页共用这一份，不要再各写一份（以前
    /// 动作审计页自己复制了一份，漏加了"代提交申请"这一项，显示成英文工具名，2026-09-29 审查发现 L4）。</summary>
    internal static string ToolDisplayName(string toolName) => toolName switch
    {
        "punch_adjust_propose"        => "补卡",
        "registration_reject_propose" => "驳回登记",
        "user_toggle_propose"         => "启用/停用",
        "user_delete_propose"         => "删除员工",
        "user_blacklist_propose"      => "拉黑/移出黑名单",
        "password_reset_propose"      => "重置密码",
        "scope_change_propose"        => "调整管理范围",
        "registration_confirm_propose" => "登记建档",
        "employee_create_propose"      => "新建员工",
        "employee_update_propose"      => "修改员工资料",
        "employee_role_propose"        => "调整角色",
        "employee_batch_toggle_propose" => "批量启停",
        "holiday_add_propose"          => "新增假期",
        "holiday_delete_propose"       => "删除假期",
        "approval_handle_propose"      => "审批处理",
        "approval_submit_on_behalf_propose" => "代提申请",
        "announcement_publish_propose" => "发布公告",
        "announcement_withdraw_propose" => "撤下公告",
        "device_register_propose"      => "登记考勤机",
        "device_update_propose"        => "修改考勤机",
        _                             => toolName
    };

    private async Task ExpireOverdueAsync(int userId, int conversationId)
    {
        await db.AgentPendingActions
            .Where(a => a.ConversationId == conversationId && a.CreatedBy == userId
                     && a.Status == AgentActionStatus.Pending && a.ExpiresAt <= DateTime.Now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentActionStatus.Expired));
    }

    // ── 确认/拒绝 ────────────────────────────────────────────────────────────

    public async Task<(bool ok, string message)> ReviewAsync(int userId, int actionId, bool approve)
    {
        var action = await db.AgentPendingActions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == actionId);
        if (action is null) return (false, "动作不存在");

        var conv = await db.AgentConversations
            .FirstOrDefaultAsync(c => c.Id == action.ConversationId && c.UserId == userId);
        if (conv is null || action.CreatedBy != userId)
            return (false, "该动作不属于你的会话");

        if (action.Status != AgentActionStatus.Pending)
            return (false, "该动作已被处理过了，请刷新查看最新状态");
        if (action.ExpiresAt <= DateTime.Now)
        {
            await db.AgentPendingActions
                .Where(a => a.Id == actionId && a.Status == AgentActionStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentActionStatus.Expired));
            return (false, "该动作已超过 15 分钟未处理，已自动作废，请让助手重新生成");
        }

        if (!approve)
        {
            // 拒绝：原子地把"待确认"改成"已拒绝"，抢不到说明已被并发处理
            var n = await db.AgentPendingActions
                .Where(a => a.Id == actionId && a.Status == AgentActionStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AgentActionStatus.Rejected)
                    .SetProperty(a => a.ReviewedBy, userId)
                    .SetProperty(a => a.ReviewedAt, DateTime.Now));
            if (n == 0) return (false, "该动作已被处理过了，请刷新查看最新状态");

            await AppendLogAsync(action, userId, "reject", true, "管理员拒绝执行");
            return (true, "已拒绝执行该动作");
        }

        // 确认：先原子认领（Pending → Approved），抢不到=已被别人/并发处理
        var claimed = await db.AgentPendingActions
            .Where(a => a.Id == actionId && a.Status == AgentActionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AgentActionStatus.Approved)
                .SetProperty(a => a.ReviewedBy, userId)
                .SetProperty(a => a.ReviewedAt, DateTime.Now));
        if (claimed == 0)
            return (false, "该动作已被处理过了，请刷新查看最新状态");

        // 执行前快照（供撤回）：先记录现场再动手
        if (UndoableTools.Contains(action.ToolName))
        {
            var snap = await PreSnapshotAsync(action, userId);
            if (snap is not null)
            {
                await db.AgentPendingActions
                    .Where(x => x.Id == actionId && x.Status == AgentActionStatus.Approved)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Undoable, true)
                        .SetProperty(x => x.SnapshotJson, snap));
            }
        }
        else
        {
            await db.AgentPendingActions
                .Where(x => x.Id == actionId && x.Status == AgentActionStatus.Approved)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Undoable, false));
        }

        // 认领成功，执行真实业务（内部重新校验范围与目标状态）
        var (ok, message) = await ExecuteApprovedAsync(action, userId);

        // 执行后快照（供撤回）：新建类动作（新员工/新申请单）动手前没有"旧状态"可抓，
        // 只能等真正建出来之后，按参数里的自然键（手机号/申请人）反查刚生成的那一条。
        if (ok && PostSnapshotTools.Contains(action.ToolName))
        {
            var postSnap = await PostSnapshotAsync(action, userId);
            if (postSnap is not null)
            {
                await db.AgentPendingActions
                    .Where(x => x.Id == actionId && x.Status == AgentActionStatus.Approved)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Undoable, true)
                        .SetProperty(x => x.SnapshotJson, postSnap));
            }
        }

        // 落库/落日志的内容与页面提示分开：重置密码这类会把一次性密码带回页面展示，
        // 但绝不写入 ResultText/日志（防明文落库）。
        var storeText = ok && action.ToolName == "password_reset_propose"
            ? "已重置密码（新密码仅在本次确认页展示一次，未存入记录）"
            : message;
        var cap = storeText.Length > 480 ? storeText[..480] : storeText;
        if (ok)
        {
            await db.AgentPendingActions
                .Where(a => a.Id == actionId && a.Status == AgentActionStatus.Approved)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResultText, cap));
        }
        else
        {
            await db.AgentPendingActions
                .Where(a => a.Id == actionId && a.Status == AgentActionStatus.Approved)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ErrorText, cap));
        }

        await AppendLogAsync(action, userId, "approve", ok, storeText);
        return (ok, message);   // message 原样回给页面（含一次性密码时仅显示这一次）
    }

    /// <summary>需要"执行后"（而不是执行前）补抓快照的动作类型——新建类，动手前无旧状态可抓。</summary>
    private static readonly HashSet<string> PostSnapshotTools =
    [
        "registration_confirm_propose",
        "employee_create_propose",
        "approval_submit_on_behalf_propose"
    ];

    /// <summary>建档/登记建档/代提申请：执行成功后补抓"建出的记录 id"作为撤回依据。</summary>
    private async Task<string?> PostSnapshotAsync(AgentPendingAction action, int operatorUserId)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(action.ParamJson) ? "{}" : action.ParamJson);
            var args = doc.RootElement;

            if (action.ToolName == "registration_confirm_propose")
            {
                var regId = GetInt(args, "registrationId");
                if (regId.HasValue)
                {
                    var userId = await db.EmployeeRegistrations
                        .Where(r => r.Id == regId.Value)
                        .Select(r => r.ConfirmedUserId)
                        .FirstOrDefaultAsync();
                    if (userId.HasValue)
                        return JsonSerializer.Serialize(new { type = "confirm", registrationId = regId.Value, userId = userId.Value });
                }
            }
            else if (action.ToolName == "employee_create_propose")
            {
                var phone = GetString(args, "phone")?.Trim();
                if (!string.IsNullOrEmpty(phone))
                {
                    var userId = await db.Users
                        .Where(u => u.Phone == phone)
                        .OrderByDescending(u => u.Id)
                        .Select(u => (int?)u.Id)
                        .FirstOrDefaultAsync();
                    if (userId.HasValue)
                        return JsonSerializer.Serialize(new { type = "create", userId = userId.Value });
                }
            }
            else if (action.ToolName == "approval_submit_on_behalf_propose")
            {
                var uid = GetInt(args, "userId");
                if (uid.HasValue)
                {
                    var reqId = await db.ApprovalRequests
                        .Where(r => r.ApplicantUserId == uid.Value)
                        .OrderByDescending(r => r.Id)
                        .Select(r => (int?)r.Id)
                        .FirstOrDefaultAsync();
                    if (reqId.HasValue)
                        return JsonSerializer.Serialize(new { type = "approvalsubmit", requestId = reqId.Value, userId = uid.Value });
                }
            }
        }
        catch { /* 快照失败不阻塞主流程 */ }
        return null;
    }

    /// <summary>执行前快照：抓"动手前"的目标状态，撤回时按它还原。</summary>
    private async Task<string?> PreSnapshotAsync(AgentPendingAction action, int operatorUserId)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(action.ParamJson) ? "{}" : action.ParamJson);
            var args = doc.RootElement;

            // 员工类：单个目标 → 快照整行关键字段
            if (action.ToolName is "user_toggle_propose" or "user_blacklist_propose" or "scope_change_propose" or "employee_update_propose" or "employee_role_propose")
            {
                var uid = GetInt(args, "userId");
                if (!uid.HasValue) return null;
                var row = await db.Users.AsNoTracking()
                    .Where(u => u.Id == uid.Value)
                    .Select(u => new
                    {
                        u.Id, u.RealName, u.DepartmentId, u.AttendanceGroupId, u.SupervisorUserId,
                        u.Phone, u.Position, u.ContractCompany, u.HireDate, u.Role, u.IsActive, u.IsBlacklisted,
                        u.ScopedDepartmentId, u.EmployeeNo
                    })
                    .FirstOrDefaultAsync();
                return row is null ? null : JsonSerializer.Serialize(new { type = "user", row });
            }
            if (action.ToolName == "employee_batch_toggle_propose")
            {
                var ids = IntListArg(args, "userIds");
                var rows = await db.Users.AsNoTracking()
                    .Where(u => ids.Contains(u.Id))
                    .Select(u => new
                    {
                        u.Id, u.RealName, u.DepartmentId, u.AttendanceGroupId, u.SupervisorUserId,
                        u.Phone, u.Position, u.ContractCompany, u.HireDate, u.Role, u.IsActive, u.IsBlacklisted,
                        u.ScopedDepartmentId, u.EmployeeNo
                    })
                    .ToListAsync();
                return rows.Count == 0 ? null : JsonSerializer.Serialize(new { type = "users", rows });
            }
            if (action.ToolName == "registration_reject_propose")
            {
                var regId = GetInt(args, "registrationId");
                if (!regId.HasValue) return null;
                var row = await db.EmployeeRegistrations.AsNoTracking()
                    .Where(r => r.Id == regId.Value)
                    .Select(r => new { r.Id, r.Phone, r.IdNumber })
                    .FirstOrDefaultAsync();
                return row is null ? null : JsonSerializer.Serialize(new { type = "reg", row });
            }
            if (action.ToolName == "punch_adjust_propose")
            {
                var uid = GetInt(args, "userId");
                var workDateS = GetString(args, "workDate");
                if (!uid.HasValue || !DateOnly.TryParse(workDateS, out var wd)) return null;
                var rec = await db.AttendanceRecords.AsNoTracking()
                    .Where(r => r.UserId == uid.Value && r.WorkDate == wd)
                    .Select(r => new { r.Id, r.ClockInTime, r.ClockOutTime, r.ApprovalNote, r.AttendanceStatus, r.LateMinutes, r.EarlyLeaveMinutes, r.ActualWorkHours })
                    .FirstOrDefaultAsync();
                var payload = new
                {
                    type = "punch", userId = uid.Value, workDate = wd.ToString("yyyy-MM-dd"),
                    existed = rec is not null,
                    clockIn = rec?.ClockInTime, clockOut = rec?.ClockOutTime, approvalNote = rec?.ApprovalNote,
                    // 补卡会让主项目重算状态/迟到早退/工时；撤回时这些列要一并还原，只还原打卡时间会让工时和状态残留
                    attendanceStatus = rec is null ? (int?)null : (int)rec.AttendanceStatus,
                    lateMinutes = rec?.LateMinutes, earlyLeaveMinutes = rec?.EarlyLeaveMinutes, actualWorkHours = rec?.ActualWorkHours
                };
                return JsonSerializer.Serialize(payload);
            }
            if (action.ToolName == "holiday_add_propose")
            {
                // 新增前没有行可抓，快照"将要添加的内容"，撤回时按同字段找到并删除
                var payload = new
                {
                    type = "holidayadd",
                    date = GetString(args, "date"),
                    name = GetString(args, "name"),
                    typeName = GetString(args, "type"),
                    groupId = GetInt(args, "groupId")
                };
                return JsonSerializer.Serialize(payload);
            }
            if (action.ToolName == "holiday_delete_propose")
            {
                var hid = GetInt(args, "holidayId");
                if (!hid.HasValue) return null;
                var row = await db.Holidays.AsNoTracking()
                    .Where(h => h.Id == hid.Value)
                    .Select(h => new { h.Id, h.HolidayName, h.HolidayDate, h.HolidayType, h.AttendanceGroupId, h.Description })
                    .FirstOrDefaultAsync();
                return row is null ? null : JsonSerializer.Serialize(new { type = "holidaydel", row });
            }
        }
        catch { /* 快照失败不阻塞主流程 */ }
        return null;
    }

    // ── 撤回（Undo）───────────────────────────────────────────────────────────

    public async Task<(bool ok, string message)> UndoAsync(int userId, int actionId)
    {
        var action = await db.AgentPendingActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == actionId);
        if (action is null) return (false, "动作不存在");

        var conv = await db.AgentConversations.FirstOrDefaultAsync(c => c.Id == action.ConversationId && c.UserId == userId);
        if (conv is null || action.CreatedBy != userId)
            return (false, "该动作不属于你的会话");

        if (!action.Undoable || action.Status != AgentActionStatus.Approved || action.ErrorText is not null)
            return (false, "该动作不可撤回（可能未成功执行，或类型本身不可逆）");
        if (action.UndoneAt.HasValue)
            return (false, "该动作已被撤回过了");
        if (string.IsNullOrWhiteSpace(action.SnapshotJson))
            return (false, "缺少撤回快照，无法撤回（请人工核对处理）");

        // 原子认领"撤回中"状态（UndoneAt 占位），失败则回滚占位以便重试
        var claimed = await db.AgentPendingActions
            .Where(a => a.Id == actionId && a.Status == AgentActionStatus.Approved && !a.UndoneAt.HasValue)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.UndoneAt, DateTime.Now)
                .SetProperty(a => a.UndoneBy, userId));
        if (claimed == 0) return (false, "该动作正在被撤回或已被撤回");

        try
        {
            var (ok, msg) = await RestoreAsync(action, userId);
            if (!ok)
            {
                await db.AgentPendingActions.Where(a => a.Id == actionId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.UndoneAt, (DateTime?)null).SetProperty(a => a.UndoneBy, (int?)null));
                return (false, msg);
            }
            await AppendLogAsync(action, userId, "undo", true, "撤回：" + msg);
            return (true, "已撤回：" + msg);
        }
        catch (Exception ex)
        {
            await db.AgentPendingActions.Where(a => a.Id == actionId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.UndoneAt, (DateTime?)null).SetProperty(a => a.UndoneBy, (int?)null));
            logger.LogWarning(ex, "AGENT 撤回失败：动作 {ActionId}", actionId);
            return (false, $"撤回失败：{ex.Message}");
        }
    }

    /// <summary>按快照还原现场（每种动作一种还原逻辑）。</summary>
    private async Task<(bool ok, string message)> RestoreAsync(AgentPendingAction action, int operatorUserId)
    {
        using var doc = JsonDocument.Parse(action.SnapshotJson!);
        var root = doc.RootElement;

        // 员工类整行还原（含范围/黑名单/在职状态）
        async Task<string> RestoreUserRowAsync(JsonElement row)
        {
            var id = row.TryGetProperty("Id", out var idEl) && idEl.TryGetInt32(out var idv) ? idv : 0;
            var u = await db.Users.FindAsync(id);
            if (u is null) return $"员工(id:{id})已不存在，无法还原";

            // 角色层级检查：撤回会把快照整行（含角色、管理范围）直接写回数据库，以前完全没查这道检查，
            // 变成了唯一能绕过它的入口——文员改了普通员工资料，之后此人被总部提拔成管理员，文员再撤回
            // 那次动作，会把管理员账号原样降级、清空管理范围（2026-09-29 第 12 轮审查发现，严重）。
            // 必须在改任何字段之前查完两道：① 目标"现在"的角色/范围管不管得到；② 快照里要恢复成的
            // 角色/范围管不管得到（防止借撤回把人恢复成管理员/不受限文员）。
            var op = await db.Users.AsNoTracking()
                .Where(x => x.Id == operatorUserId)
                .Select(x => new { x.Role, x.ScopedDepartmentId })
                .FirstOrDefaultAsync()
                ?? throw new InvalidOperationException("操作者账号不存在");
            if (!AttendanceSystem.Middlewares.CurrentUser.CanManageAccountCore(op.Role, op.ScopedDepartmentId, u.Role, u.ScopedDepartmentId))
                throw new InvalidOperationException("无权撤回：该账号现在的角色超出你的管理权限（角色层级限制）");
            var snapRole = row.TryGetProperty("Role", out var srEl) && srEl.ValueKind == JsonValueKind.Number
                ? (AttendanceSystem.Models.Enums.UserRole)srEl.GetInt32() : u.Role;
            var snapScope = RowInt(row, "ScopedDepartmentId");
            if (!AttendanceSystem.Middlewares.CurrentUser.CanManageAccountCore(op.Role, op.ScopedDepartmentId, snapRole, snapScope))
                throw new InvalidOperationException("无权撤回：要恢复成的角色超出你的管理权限（角色层级限制）");

            if (row.TryGetProperty("RealName", out var rn)) u.RealName = rn.ValueKind == JsonValueKind.String ? rn.GetString()! : u.RealName;
            u.DepartmentId = RowInt(row, "DepartmentId");
            u.AttendanceGroupId = RowInt(row, "AttendanceGroupId");
            u.SupervisorUserId = RowInt(row, "SupervisorUserId");
            if (row.TryGetProperty("Phone", out var ph)) u.Phone = ph.ValueKind == JsonValueKind.String ? ph.GetString() : null;
            if (row.TryGetProperty("Position", out var po)) u.Position = po.ValueKind == JsonValueKind.String ? po.GetString() : null;
            if (row.TryGetProperty("ContractCompany", out var cc)) u.ContractCompany = cc.ValueKind == JsonValueKind.String ? cc.GetString() : null;
            if (row.TryGetProperty("HireDate", out var hd)) u.HireDate = hd.ValueKind == JsonValueKind.String && DateOnly.TryParse(hd.GetString(), out var d) ? d : null;
            // 在职状态不直接改字段：主项目的停用/启用还要维护 DeactivatedAt、并同步考勤机，统一走 UserService（见下方保存后）
            bool? wantActive = row.TryGetProperty("IsActive", out var ia) && (ia.ValueKind == JsonValueKind.True || ia.ValueKind == JsonValueKind.False)
                ? ia.GetBoolean() : null;
            if (row.TryGetProperty("IsBlacklisted", out var ib)) u.IsBlacklisted = ib.ValueKind is JsonValueKind.True or JsonValueKind.False && ib.GetBoolean();
            if (row.TryGetProperty("Role", out var roleEl) && roleEl.ValueKind == JsonValueKind.Number)
                u.Role = (AttendanceSystem.Models.Enums.UserRole)roleEl.GetInt32();
            u.ScopedDepartmentId = RowInt(row, "ScopedDepartmentId");
            u.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
            if (wantActive.HasValue && wantActive.Value != u.IsActive)
            {
                if (wantActive.Value) await userService.ActivateUserAsync(u.Id, operatorUserId);
                else await userService.DeactivateUserAsync(u.Id, operatorUserId);
            }
            return $"已还原员工 {u.RealName}（{u.EmployeeNo}）";
        }

        static int? RowInt(JsonElement row, string name)
            => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32()
             : v.ValueKind == JsonValueKind.Null ? null : null;

        switch (root.GetProperty("type").GetString())
        {
            case "user":
            {
                var msg = await RestoreUserRowAsync(root.GetProperty("row"));
                return (true, msg);
            }
            case "users":
            {
                // 走 UserService 批量方法：主项目的停用/启用要维护 DeactivatedAt 并同步考勤机，不能直接 ExecuteUpdate 改字段
                var toActivate = new List<int>();
                var toDeactivate = new List<int>();
                foreach (var row in root.GetProperty("rows").EnumerateArray())
                {
                    var id = row.TryGetProperty("Id", out var idEl) && idEl.TryGetInt32(out var idv) ? idv : 0;
                    var wantActive = row.TryGetProperty("IsActive", out var ia) && ia.ValueKind == JsonValueKind.True;
                    (wantActive ? toActivate : toDeactivate).Add(id);
                }
                if (toActivate.Count > 0) await userService.SetActiveBatchAsync(toActivate, true, operatorUserId);
                if (toDeactivate.Count > 0) await userService.SetActiveBatchAsync(toDeactivate, false, operatorUserId);
                return (true, $"已还原 {toActivate.Count + toDeactivate.Count} 名员工的在职状态");
            }
            case "reg":
            {
                var id = root.GetProperty("row").GetProperty("Id").GetInt32();
                var phone = root.GetProperty("row").TryGetProperty("Phone", out var p) ? p.GetString() : null;
                var idNo = root.GetProperty("row").TryGetProperty("IdNumber", out var n) ? n.GetString() : null;
                // 防冲突：已有在职员工或已有待确认登记占了同一手机号/身份证 → 拒绝还原
                var dupUser = await db.Users.AnyAsync(u => u.IsActive && (phone != null && u.Phone == phone || idNo != null && u.IdNumber == idNo));
                var dupReg = await db.EmployeeRegistrations.AnyAsync(r => r.Status == RegistrationStatus.Pending && r.Id != id && (phone != null && r.Phone == phone || idNo != null && r.IdNumber == idNo));
                if (dupUser || dupReg) return (false, "该登记的手机号/身份证已有在职员工或新的待确认登记，无法自动撤回（请人工处理）");

                await db.EmployeeRegistrations.Where(r => r.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, RegistrationStatus.Pending)
                        .SetProperty(r => r.RejectReason, (string?)null)
                        .SetProperty(r => r.ReviewedAt, (DateTime?)null));
                return (true, $"登记 #{id} 已恢复为待确认");
            }
            case "confirm" or "create":
            {
                var uid = root.GetProperty("userId").GetInt32();
                var u = await db.Users.AsNoTracking().Where(x => x.Id == uid).Select(x => new { x.Id, x.RealName, x.EmployeeNo }).FirstOrDefaultAsync();
                if (u is null) return (false, "要撤回的建号记录已不存在");
                await userService.DeleteUserAsync(u.Id, operatorUserId);
                if (root.GetProperty("type").GetString() == "confirm")
                {
                    var regId = root.GetProperty("registrationId").GetInt32();
                    await db.EmployeeRegistrations.Where(r => r.Id == regId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(r => r.Status, RegistrationStatus.Pending)
                            .SetProperty(r => r.ConfirmedUserId, (int?)null)
                            .SetProperty(r => r.ReviewedAt, (DateTime?)null));
                    return (true, $"已删除新建员工 {u.RealName}（{u.EmployeeNo}）并把登记 #{regId} 恢复为待确认");
                }
                return (true, $"已删除新建员工 {u.RealName}（{u.EmployeeNo}）");
            }
            case "punch":
            {
                var uid = root.GetProperty("userId").GetInt32();
                var wd = DateOnly.Parse(root.GetProperty("workDate").GetString()!);
                var existed = root.GetProperty("existed").GetBoolean();
                var ci = root.GetProperty("clockIn").ValueKind == JsonValueKind.Null ? (DateTime?)null : root.GetProperty("clockIn").GetDateTime();
                var co = root.GetProperty("clockOut").ValueKind == JsonValueKind.Null ? (DateTime?)null : root.GetProperty("clockOut").GetDateTime();
                var note = root.GetProperty("approvalNote").ValueKind == JsonValueKind.Null ? null : root.GetProperty("approvalNote").GetString();

                var rec = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == uid && r.WorkDate == wd);
                if (!existed)
                {
                    if (rec is not null) { db.AttendanceRecords.Remove(rec); await db.SaveChangesAsync(); }
                }
                else
                {
                    if (rec is null) { rec = new Models.Entities.AttendanceRecord { UserId = uid, WorkDate = wd, UpdatedAt = DateTime.Now }; db.AttendanceRecords.Add(rec); }
                    rec.ClockInTime = ci; rec.ClockOutTime = co; rec.ApprovalNote = note; rec.UpdatedAt = DateTime.Now;
                    // 旧快照没有这几项时跳过（向后兼容）
                    if (root.TryGetProperty("attendanceStatus", out var stEl) && stEl.ValueKind == JsonValueKind.Number)
                        rec.AttendanceStatus = (AttendanceStatus)stEl.GetInt32();
                    if (root.TryGetProperty("lateMinutes", out var lmEl) && lmEl.ValueKind == JsonValueKind.Number) rec.LateMinutes = lmEl.GetInt32();
                    if (root.TryGetProperty("earlyLeaveMinutes", out var emEl) && emEl.ValueKind == JsonValueKind.Number) rec.EarlyLeaveMinutes = emEl.GetInt32();
                    if (root.TryGetProperty("actualWorkHours", out var whEl) && whEl.ValueKind == JsonValueKind.Number) rec.ActualWorkHours = whEl.GetDecimal();
                    await db.SaveChangesAsync();
                }
                await attendanceService.GenerateMonthlySummaryAsync(wd.Year, wd.Month, new[] { uid });
                return (true, $"已还原 {wd:yyyy-MM-dd} 的打卡记录");
            }
            case "holidayadd":
            {
                var dateS = root.TryGetProperty("date", out var dt) ? dt.GetString() : null;
                var name = root.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                var typeS = root.TryGetProperty("typeName", out var tp) ? tp.GetString() : null;
                var gid = root.TryGetProperty("groupId", out var gg) && gg.ValueKind == JsonValueKind.Number ? gg.GetInt32() : (int?)null;
                if (!DateOnly.TryParse(dateS, out var date) || string.IsNullOrEmpty(name)) return (false, "快照缺少日期/名称");
                var holiday = await db.Holidays.FirstOrDefaultAsync(h => h.HolidayDate == date && h.HolidayName == name && h.AttendanceGroupId == gid);
                if (holiday is null) return (false, "要撤回的假期已不存在");
                db.Holidays.Remove(holiday);
                await db.SaveChangesAsync();
                return (true, $"已删除撤回的假期 {date:yyyy-MM-dd} {name}");
            }
            case "holidaydel":
            {
                var row = root.GetProperty("row");
                var name = row.GetProperty("HolidayName").GetString()!;
                var date = DateOnly.Parse(row.GetProperty("HolidayDate").GetString()!);   // DateOnly 序列化为 yyyy-MM-dd
                var typeVal = row.GetProperty("HolidayType").GetInt32();
                var gid = row.GetProperty("AttendanceGroupId").ValueKind == JsonValueKind.Number ? row.GetProperty("AttendanceGroupId").GetInt32() : (int?)null;
                var desc = row.TryGetProperty("Description", out var de) && de.ValueKind == JsonValueKind.String ? de.GetString() : null;
                var dup = await db.Holidays.AnyAsync(h => h.HolidayDate == date && h.HolidayName == name && h.AttendanceGroupId == gid);
                if (dup) return (false, "该假期已被重新添加，无需重复恢复");
                db.Holidays.Add(new Models.Entities.Holiday
                {
                    HolidayName = name,
                    HolidayDate = date,
                    HolidayType = (AttendanceSystem.Models.Enums.HolidayType)typeVal,
                    AttendanceGroupId = gid,
                    Description = desc,
                    CreatedAt = DateTime.Now
                });
                await db.SaveChangesAsync();
                return (true, $"已恢复假期 {date:yyyy-MM-dd} {name}");
            }
            case "approvalsubmit":
            {
                var reqId = root.GetProperty("requestId").GetInt32();
                var applicantId = root.GetProperty("userId").GetInt32();
                var req = await db.ApprovalRequests.AsNoTracking()
                    .Where(r => r.Id == reqId)
                    .Select(r => new { r.RequestNo, r.ApprovalStatus })
                    .FirstOrDefaultAsync();
                if (req is null) return (false, "要撤回的申请单已不存在");
                if (req.ApprovalStatus != ApprovalStatus.Pending)
                    return (false, $"该申请单当前状态为「{req.ApprovalStatus}」，已被审批人处理，无法自动撤回（请到审批记录里人工处理）");
                var cancelled = await approvalService.CancelApprovalAsync(applicantId, reqId);
                return cancelled ? (true, $"已撤销申请单 [{req.RequestNo}]") : (false, "撤销失败（可能刚被处理，请刷新核对）");
            }
            default:
                return (false, "不支持的撤回类型");
        }
    }

    private async Task AppendLogAsync(AgentPendingAction action, int reviewerId, string reviewAction, bool success, string detail)
    {
        db.AgentActionLogs.Add(new AgentActionLog
        {
            ConversationId = action.ConversationId,
            OperatorUserId = action.CreatedBy,
            ApproverUserId = reviewerId,
            ToolName       = action.ToolName,
            SummaryText    = action.SummaryText,
            ReviewAction   = reviewAction,
            Success        = success,
            WasExpired     = false,
            DetailText     = detail,
            CreatedAt      = DateTime.Now
        });
        await db.SaveChangesAsync();
    }

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
                "holiday_add_propose"          => await ExecuteAddHolidayAsync(operatorUserId, args),
                "holiday_delete_propose"       => await ExecuteDeleteHolidayAsync(operatorUserId, args),
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
            return (false, ex.Message);
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
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId })
            .FirstOrDefaultAsync();
        if (user is null) return (false, "目标员工不存在");
        if (!await deptScope.CanAccessDeptAsync(cu, user.DepartmentId))
            return (false, "该员工不在你的管理范围内，无权补卡");

        var clockInS  = GetString(args, "clockIn");
        var clockOutS = GetString(args, "clockOut");
        var remark    = GetString(args, "remark");

        var clockIn  = ParseTime(clockInS, workDate);
        var clockOut = ParseTime(clockOutS, workDate);
        if (clockIn is null && clockOut is null)
            return (false, "上班/下班打卡时间至少要填一个");

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
        return (true, $"已彻底删除员工 {user.RealName}（{user.EmployeeNo}）。该操作不可恢复，其考勤/审批历史已一并清除。");
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
                              ?? throw new InvalidOperationException("该部门无法自动生成工号，请手动填写");
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
                CreatedAt             = DateTime.Now,
                UpdatedAt             = DateTime.Now
            };
            await userService.CreateUserAsync(newUser, "123456");   // 固定初始密码，首登强制改
            await registrationService.MarkConfirmedAsync(reg.Id, newUser.Id);
            return (true, $"已将登记 #{reg.Id}（{reg.RealName}）建档：工号 {finalEmployeeNo}，部门【{deptName}】，直属上级 {sup.RealName}。初始密码 123456（系统不会强制改密，请提醒本人自行修改）");
        }
        catch (Exception ex)
        {
            // 建档失败：登记已被认领消费（与页面行为一致），提示管理员在员工管理页善后
            logger.LogWarning(ex, "AGENT 认领建档失败：登记 #{RegId}（目标工号 {Eno}）", reg.Id, finalEmployeeNo);
            return (false, $"建档失败：{ex.Message}（该登记已被认领，请到\"员工管理→待确认\"核对是否需要人工补建）");
        }
    }

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
                       ?? throw new InvalidOperationException("该部门无法自动生成工号，请手动填写");
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
            CreatedAt         = DateTime.Now,
            UpdatedAt         = DateTime.Now
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

        user.UpdatedAt = DateTime.Now;
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
        u.UpdatedAt = DateTime.Now;
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

    /// <summary>假期写权限：全公司(null 组)仅总部；组假期须"关联部门全部在范围内"（比页面更严）。</summary>
    private async Task<bool> HolidayWritableAsync(int? groupId, HashSet<int>? visibleIds, AttendanceSystem.Middlewares.CurrentUser cu)
    {
        if (visibleIds is null) return true;
        if (!groupId.HasValue) return false;   // 全公司假期仅总部
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return false;
        return deptIds.All(visibleIds.Contains);
    }

    private async Task<(bool, string)> ExecuteAddHolidayAsync(int operatorUserId, JsonElement args)
    {
        var dateS = GetString(args, "date");
        var typeS = GetString(args, "type");
        var groupId = GetInt(args, "groupId");
        var name = GetString(args, "name")?.Trim();
        if (!DateOnly.TryParse(dateS, out var date)) return (false, "date 格式不正确");
        var type = typeS switch
        {
            "legal" or "LegalHoliday"             => AttendanceSystem.Models.Enums.HolidayType.LegalHoliday,
            "rest" or "CompanyRestDay"            => AttendanceSystem.Models.Enums.HolidayType.CompanyRestDay,
            "compensatory" or "CompensatoryWorkDay" => AttendanceSystem.Models.Enums.HolidayType.CompensatoryWorkDay,
            _ => (AttendanceSystem.Models.Enums.HolidayType?)null
        };
        if (!type.HasValue) return (false, "type 不合法");
        if (string.IsNullOrEmpty(name)) return (false, "缺少假期名称");

        var (cu, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);
        if (!await HolidayWritableAsync(groupId, visibleIds, cu))
            return (false, "无权给该范围设置假期（全公司假期仅总部；组假期需其部门全在范围内）");

        db.Holidays.Add(new Models.Entities.Holiday
        {
            HolidayName       = name,
            HolidayDate       = date,
            HolidayType       = type!.Value,
            AttendanceGroupId = groupId,
            CreatedAt         = DateTime.Now
        });
        await db.SaveChangesAsync();
        return (true, $"已添加假期：{date:yyyy-MM-dd} {name}" + (groupId.HasValue ? "（考勤组专属）" : "（全公司）"));
    }

    private async Task<(bool, string)> ExecuteDeleteHolidayAsync(int operatorUserId, JsonElement args)
    {
        var holidayId = GetInt(args, "holidayId");
        if (!holidayId.HasValue) return (false, "参数缺失：holidayId");

        var (cu, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);

        var h = await db.Holidays.FindAsync(holidayId.Value);
        if (h is null) return (false, "该假期不存在");
        if (!await HolidayWritableAsync(h.AttendanceGroupId, visibleIds, cu))
            return (false, "无权删除该假期（全公司假期仅总部；组假期需其部门全在范围内）");

        db.Holidays.Remove(h);
        await db.SaveChangesAsync();
        return (true, $"已删除假期：{h.HolidayDate:yyyy-MM-dd} {h.HolidayName}");
    }

    // ── 审批处理 / 公告发布撤回 / 考勤机登记变更（执行侧）────────────────────────

    private async Task<(bool, string)> ExecuteHandleApprovalAsync(int operatorUserId, JsonElement args)
    {
        var requestId = GetInt(args, "requestId");
        var approve = args.TryGetProperty("approve", out var av) && av.ValueKind is JsonValueKind.True or JsonValueKind.False ? av.GetBoolean() : (bool?)null;
        var comment = GetString(args, "comment")?.Trim();
        if (!requestId.HasValue || !approve.HasValue) return (false, "参数不完整：requestId/approve");
        if (!approve.Value && string.IsNullOrWhiteSpace(comment)) return (false, "驳回必须填写意见");

        var mine = await db.ApprovalSteps.AsNoTracking()
            .Where(s => s.ApprovalRequestId == requestId.Value && s.ApproverUserId == operatorUserId
                     && s.ApprovalStatus == ApprovalStatus.Pending)
            .Select(s => new { ReqNo = s.ApprovalRequest.RequestNo })
            .FirstOrDefaultAsync();
        if (mine is null) return (false, "没有找到指派给你且待处理的这张单（可能已被处理）");

        var ok = await approvalService.HandleApprovalAsync(operatorUserId, new AttendanceSystem.Models.DTOs.HandleApprovalDto
        {
            ApprovalRequestId = requestId.Value,
            IsApproved        = approve.Value,
            Comment           = comment
        });
        return ok
            ? (true, $"已{(approve.Value ? "通过" : "驳回")}审批单 [{mine.ReqNo}]")
            : (false, "处理失败：该单状态已变化或流程不允许，请刷新核对");
    }

    /// <summary>代员工提交请假/加班/出差申请：仍进入正常审批流程（指派给该员工的审批人），不是直接生效。</summary>
    private async Task<(bool, string)> ExecuteApprovalSubmitOnBehalfAsync(int operatorUserId, JsonElement args)
    {
        var userId = GetInt(args, "userId");
        var type = GetString(args, "type");
        var startS = GetString(args, "startTime");
        var endS = GetString(args, "endTime");
        var leaveTypeI = GetInt(args, "leaveType");
        var destination = GetString(args, "destination");
        var reason = GetString(args, "reason");
        var approverUserId = GetInt(args, "approverUserId");
        if (!userId.HasValue || type is not ("leave" or "overtime" or "businesstrip")
            || !DateTime.TryParse(startS, out var start) || !DateTime.TryParse(endS, out var end))
            return (false, "参数不正确：需要 userId/type/startTime/endTime");

        var (_, visibleIds, ok0, err0) = await LoadOperatorAsync(operatorUserId);
        if (!ok0) return (false, err0!);

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.IsActive, u.IsBlacklisted })
            .FirstOrDefaultAsync();
        if (target is null) return (false, "目标员工不存在");
        if (!target.IsActive || target.IsBlacklisted) return (false, "该员工已停用或在黑名单中，不能代为提交申请");
        if (visibleIds is not null && (target.DepartmentId is null || !visibleIds.Contains(target.DepartmentId.Value)))
            return (false, "该员工不在你的管理范围内");

        var dto = new AttendanceSystem.Models.DTOs.SubmitApprovalDto { Reason = reason, ApproverUserId = approverUserId };
        switch (type)
        {
            case "leave":
                dto.ApprovalType = ApprovalType.Leave;
                dto.LeaveType = leaveTypeI.HasValue ? (LeaveType)leaveTypeI.Value : null;
                dto.LeaveStartTime = start; dto.LeaveEndTime = end;
                break;
            case "overtime":
                dto.ApprovalType = ApprovalType.Overtime;
                dto.OvertimeStartTime = start; dto.OvertimeEndTime = end;
                break;
            default:
                dto.ApprovalType = ApprovalType.BusinessTrip;
                dto.BusinessTripStartTime = start; dto.BusinessTripEndTime = end; dto.BusinessTripDestination = destination;
                break;
        }

        var request = await approvalService.SubmitApprovalAsync(target.Id, dto);
        return (true, $"已为 {target.RealName}（{target.EmployeeNo}）提交申请单 [{request.RequestNo}]，已进入正常审批流程");
    }

    private async Task<(bool, string)> ExecutePublishAnnouncementAsync(int operatorUserId, JsonElement args)
    {
        var title   = GetString(args, "title")?.Trim();
        var content = GetString(args, "content")?.Trim();
        var scopeS  = GetString(args, "scope");
        var scopeId = GetInt(args, "scopeId");
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(content)) return (false, "缺少标题或正文");
        if (title.Length > 200 || content.Length > 2000) return (false, "标题≤200字、正文≤2000字");

        AttendanceSystem.Models.Enums.AnnouncementScopeType? scope = scopeS switch
        {
            "all"             => AttendanceSystem.Models.Enums.AnnouncementScopeType.All,
            "department"      => AttendanceSystem.Models.Enums.AnnouncementScopeType.Department,
            "attendancegroup" => AttendanceSystem.Models.Enums.AnnouncementScopeType.AttendanceGroup,
            _                 => null
        };
        if (!scope.HasValue) return (false, "scope 不合法");

        var (cu, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);

        if (scope == AttendanceSystem.Models.Enums.AnnouncementScopeType.All)
        {
            if (cu.Role != AttendanceSystem.Models.Enums.UserRole.Admin || cu.ScopedDepartmentId.HasValue)
                return (false, "全公司公告只有总部能发");
        }
        else if (scope == AttendanceSystem.Models.Enums.AnnouncementScopeType.Department)
        {
            if (!scopeId.HasValue) return (false, "缺 scopeId");
            if (visibleIds is not null && !visibleIds.Contains(scopeId.Value)) return (false, "部门不在你的管理范围内");
        }
        else
        {
            if (!scopeId.HasValue) return (false, "缺 scopeId");
            var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == scopeId.Value).Select(d => d.Id).ToListAsync();
            if (visibleIds is not null && (deptIds.Count == 0 || !deptIds.All(visibleIds.Contains)))
                return (false, "无权给该考勤组发公告（含范围外部门/成员）");
            var memberDepts = await db.Users.AsNoTracking()
                .Where(u => u.IsActive && u.AttendanceGroupId == scopeId.Value)
                .Select(u => u.DepartmentId).ToListAsync();
            if (visibleIds is not null && memberDepts.Any(d => !d.HasValue || !visibleIds.Contains(d.Value)))
                return (false, "该考勤组含范围外/无部门成员，不能发布（改走部门范围或联系总部）");
        }

        var ann = await announcementService.PublishAsync(operatorUserId, cu.Role, new AttendanceSystem.Models.DTOs.PublishAnnouncementDto
        {
            Title     = title,
            Content   = content,
            ScopeType = scope.Value,
            ScopeId   = scopeId
        });
        return (true, $"已发布公告《{ann.Title}》（ID:{ann.Id}），并已通知范围内员工");
    }

    private async Task<(bool, string)> ExecuteWithdrawAnnouncementAsync(int operatorUserId, JsonElement args)
    {
        var annId = GetInt(args, "announcementId");
        if (!annId.HasValue) return (false, "参数缺失：announcementId");

        var (cu, _, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);

        var ann = await db.Announcements.AsNoTracking()
            .Where(a => a.Id == annId.Value)
            .Select(a => new { a.Id, a.Title, a.PublisherUserId })
            .FirstOrDefaultAsync();
        if (ann is null) return (false, "公告不存在");
        var isHq = cu.Role == AttendanceSystem.Models.Enums.UserRole.Admin && cu.ScopedDepartmentId is null;
        if (ann.PublisherUserId != operatorUserId && !isHq)
            return (false, "只能撤回自己发布的公告（总部可撤回任意公告）");

        var done = await announcementService.WithdrawAsync(operatorUserId, isManager: true, ann.Id);
        return done ? (true, $"已撤下公告《{ann.Title}》") : (false, "撤回公告失败（可能已被撤）");
    }

    private async Task<(bool, string)> ExecuteRegisterDeviceAsync(int operatorUserId, JsonElement args)
    {
        var sn   = GetString(args, "sn")?.Trim();
        var name = GetString(args, "name")?.Trim();
        var dept = GetInt(args, "departmentId");
        if (string.IsNullOrEmpty(sn) || !System.Text.RegularExpressions.Regex.IsMatch(sn, @"^[A-Za-z0-9._-]{1,50}$"))
            return (false, "序列号不合法（字母/数字/点/横线/下划线，≤50）");
        if (await db.ZKDevices.AnyAsync(d => d.SN == sn)) return (false, "该序列号已登记");

        var (_, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);
        if (visibleIds is not null)
        {
            if (!dept.HasValue) return (false, "受限管理员登记设备必须指定归属部门");
            if (!visibleIds.Contains(dept.Value)) return (false, "归属部门不在你的管理范围内");
        }
        else if (dept.HasValue && !await db.Departments.AnyAsync(d => d.Id == dept.Value))
            return (false, "归属部门不存在");

        db.ZKDevices.Add(new Models.Entities.ZKDevice
        {
            SN           = sn,
            Name         = string.IsNullOrEmpty(name) ? null : name,
            DepartmentId = dept,
            IsActive     = true,
            CreatedAt    = DateTime.Now
        });
        await db.SaveChangesAsync();
        return (true, $"已登记考勤机 SN:{sn}" + (string.IsNullOrEmpty(name) ? "" : $"（{name}）") + (dept.HasValue ? "" : "（总部共用/未归类）"));
    }

    private async Task<(bool, string)> ExecuteUpdateDeviceAsync(int operatorUserId, JsonElement args)
    {
        var deviceId = GetInt(args, "deviceId");
        var name = GetString(args, "name")?.Trim();
        var active = args.TryGetProperty("active", out var av) && av.ValueKind is JsonValueKind.True or JsonValueKind.False ? av.GetBoolean() : (bool?)null;
        var deptParam = GetInt(args, "departmentId");
        int? newDept = deptParam.HasValue && deptParam.Value == 0 ? null : deptParam;
        if (!deviceId.HasValue) return (false, "参数缺失：deviceId");
        if (name is null && !active.HasValue && !deptParam.HasValue) return (false, "没有要修改的字段");

        var (_, visibleIds, ok, err) = await LoadOperatorAsync(operatorUserId);
        if (!ok) return (false, err!);

        var d = await db.ZKDevices.FindAsync(deviceId.Value);
        if (d is null) return (false, "考勤机不存在");
        if (visibleIds is not null)
        {
            if (d.DepartmentId is null || !visibleIds.Contains(d.DepartmentId.Value))
                return (false, "该考勤机不在你的管理范围内");
            if (newDept.HasValue && !visibleIds.Contains(newDept.Value))
                return (false, "新归属部门不在你的管理范围内");
        }
        else if (newDept.HasValue && !await db.Departments.AnyAsync(x => x.Id == newDept.Value))
            return (false, "新归属部门不存在");

        if (name is not null) d.Name = string.IsNullOrEmpty(name) ? null : name;
        if (active.HasValue) d.IsActive = active.Value;
        if (deptParam.HasValue) d.DepartmentId = newDept;
        await db.SaveChangesAsync();
        return (true, $"已更新考勤机 {d.Name ?? d.SN}（SN:{d.SN}）");
    }
}
