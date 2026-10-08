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

/// <summary>确认/拒绝动作，以及执行前后的快照（<see cref="AgentActionService"/> 的一部分）。</summary>
public partial class AgentActionService
{
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
        if (action.ExpiresAt <= clock.LocalNow())
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
                    .SetProperty(a => a.ReviewedAt, clock.LocalNow()));
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
                .SetProperty(a => a.ReviewedAt, clock.LocalNow()));
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
        // 执行失败：丢弃执行到一半、已经改在内存里的实体，不能让下面写日志的 SaveChanges 顺手存进库
        if (!ok) db.ChangeTracker.Clear();

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
        }
        catch { /* 快照失败不阻塞主流程 */ }
        return null;
    }
}
