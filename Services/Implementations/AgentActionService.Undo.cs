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

/// <summary>撤回已执行的动作（<see cref="AgentActionService"/> 的一部分）。</summary>
public partial class AgentActionService
{
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
                .SetProperty(a => a.UndoneAt, clock.LocalNow())
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
            return (false, $"撤回失败：{AgentErrorText.ForUser(ex)}");
        }
    }

    /// <summary>按快照还原现场（每种动作一种还原逻辑）。</summary>
    private async Task<(bool ok, string message)> RestoreAsync(AgentPendingAction action, int operatorUserId)
    {
        using var doc = JsonDocument.Parse(action.SnapshotJson!);
        var root = doc.RootElement;

        switch (root.GetProperty("type").GetString())
        {
            case "user": return await RestoreSingleUserAsync(root, operatorUserId);
            case "users": return await RestoreUsersActiveStateAsync(root, operatorUserId);
            case "reg": return await RestoreRegistrationAsync(root);
            case "confirm" or "create": return await RestoreCreatedUserAsync(root, operatorUserId);
            case "punch": return await RestorePunchAsync(root);
            case "approvalsubmit": return await RestoreApprovalSubmitAsync(root);
            default:
                return (false, "不支持的撤回类型");
        }
    }

    /// <summary>员工类整行还原（含范围/黑名单/在职状态），做完角色层级检查才写回。</summary>
    private async Task<string> RestoreUserRowAsync(JsonElement row, int operatorUserId)
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
            ?? throw new BusinessException("操作者账号不存在");
        if (!AttendanceSystem.Middlewares.CurrentUser.CanManageAccountCore(op.Role, op.ScopedDepartmentId, u.Role, u.ScopedDepartmentId))
            throw new BusinessException("无权撤回：该账号现在的角色超出你的管理权限（角色层级限制）");
        var snapRole = row.TryGetProperty("Role", out var srEl) && srEl.ValueKind == JsonValueKind.Number
            ? (AttendanceSystem.Models.Enums.UserRole)srEl.GetInt32() : u.Role;
        var snapScope = RowInt(row, "ScopedDepartmentId");
        if (!AttendanceSystem.Middlewares.CurrentUser.CanManageAccountCore(op.Role, op.ScopedDepartmentId, snapRole, snapScope))
            throw new BusinessException("无权撤回：要恢复成的角色超出你的管理权限（角色层级限制）");

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
        u.UpdatedAt = clock.LocalNow();
        await db.SaveChangesAsync();
        if (wantActive.HasValue && wantActive.Value != u.IsActive)
        {
            if (wantActive.Value) await userService.ActivateUserAsync(u.Id, operatorUserId);
            else await userService.DeactivateUserAsync(u.Id, operatorUserId);
        }
        return $"已还原员工 {u.RealName}（{u.EmployeeNo}）";
    }

    private static int? RowInt(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32()
         : v.ValueKind == JsonValueKind.Null ? null : null;

    /// <summary>还原单个员工整行（含范围/黑名单/在职状态）（从 <see cref="RestoreAsync"/> 里原样搬出）。</summary>
    private async Task<(bool ok, string message)> RestoreSingleUserAsync(JsonElement root, int operatorUserId)
    {
        var msg = await RestoreUserRowAsync(root.GetProperty("row"), operatorUserId);
        return (true, msg);
    }

    /// <summary>还原批量启停之前各员工的在职状态（从 <see cref="RestoreAsync"/> 里原样搬出）。</summary>
    private async Task<(bool ok, string message)> RestoreUsersActiveStateAsync(JsonElement root, int operatorUserId)
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

    /// <summary>把被驳回的登记恢复为待确认（从 <see cref="RestoreAsync"/> 里原样搬出）。</summary>
    private async Task<(bool ok, string message)> RestoreRegistrationAsync(JsonElement root)
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

    /// <summary>撤回新建/确认建档：删除新建的员工，确认建档的还要把登记恢复为待确认（从 <see cref="RestoreAsync"/> 里原样搬出）。</summary>
    private async Task<(bool ok, string message)> RestoreCreatedUserAsync(JsonElement root, int operatorUserId)
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

    /// <summary>还原手动补卡之前的打卡记录（从 <see cref="RestoreAsync"/> 里原样搬出）。</summary>
    private async Task<(bool ok, string message)> RestorePunchAsync(JsonElement root)
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
            if (rec is null) { rec = new Models.Entities.AttendanceRecord { UserId = uid, WorkDate = wd, UpdatedAt = clock.LocalNow() }; db.AttendanceRecords.Add(rec); }
            rec.ClockInTime = ci; rec.ClockOutTime = co; rec.ApprovalNote = note; rec.UpdatedAt = clock.LocalNow();
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

    /// <summary>撤销代提交的申请单（仅限还在待审批的）（从 <see cref="RestoreAsync"/> 里原样搬出）。</summary>
    private async Task<(bool ok, string message)> RestoreApprovalSubmitAsync(JsonElement root)
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
            CreatedAt      = clock.LocalNow()
        });
        await db.SaveChangesAsync();
    }
}
