using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>审批、公告、考勤机、员工建档/改资料/批量启停的提案（<see cref="AgentToolExecutor"/> 的一部分）。</summary>
public partial class AgentToolExecutor
{
    // ── 审批（读：待我审批 / 提案：处理） ───────────────────────────────────────

    private async Task<string> ApprovalPendingListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var limit = Math.Clamp(IntArg(doc.RootElement, "limit") ?? 30, 1, 50);

        var rows = await db.ApprovalSteps.AsNoTracking()
            .Where(s => s.ApproverUserId == operatorUserId && s.ApprovalStatus == ApprovalStatus.Pending)
            .OrderByDescending(s => s.ApprovalRequest.SubmittedAt)
            .Take(limit)
            .Select(s => new
            {
                s.Id,
                s.ApprovalRequestId,
                ReqNo = s.ApprovalRequest.RequestNo,
                Type = s.ApprovalRequest.ApprovalType,
                Applicant = s.ApprovalRequest.Applicant.RealName,
                Eno = s.ApprovalRequest.Applicant.EmployeeNo,
                Reason = s.ApprovalRequest.Reason,
                s.ApprovalRequest.SubmittedAt,
                s.ApprovalRequest.PunchDate,
                s.ApprovalRequest.LeaveStartTime,
                s.ApprovalRequest.LeaveEndTime,
                s.ApprovalRequest.OvertimeStartTime,
                s.ApprovalRequest.OvertimeEndTime,
                s.ApprovalRequest.BusinessTripStartTime,
                s.ApprovalRequest.BusinessTripEndTime,
                s.ApprovalRequest.BusinessTripDestination
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return "当前没有待你审批的单子。";

        var sb = new System.Text.StringBuilder();
        sb.Append($"共 {rows.Count} 张待你审批（用 approval_handle_propose 处理，requestId 见 #号）：\n");
        foreach (var r in rows)
        {
            string range = r.Type switch
            {
                ApprovalType.PunchReplenishment => $"补卡日期:{r.PunchDate:yyyy-MM-dd}",
                ApprovalType.Leave              => $"时间:{r.LeaveStartTime:MM-dd HH:mm}~{r.LeaveEndTime:MM-dd HH:mm}",
                ApprovalType.Overtime           => $"时间:{r.OvertimeStartTime:MM-dd HH:mm}~{r.OvertimeEndTime:MM-dd HH:mm}",
                _                               => $"时间:{r.BusinessTripStartTime:MM-dd HH:mm}~{r.BusinessTripEndTime:MM-dd HH:mm}{(string.IsNullOrEmpty(r.BusinessTripDestination) ? "" : "，去 " + r.BusinessTripDestination)}"
            };
            var reasonPreview = (r.Reason ?? "").Length > 30 ? r.Reason![..30] + "…" : (r.Reason ?? "-");
            sb.AppendLine($"#{r.ApprovalRequestId} [{r.ReqNo}] {r.Applicant}({r.Eno}) {r.Type.ToDisplayName()} {range} 理由:{reasonPreview} 提交:{r.SubmittedAt:MM-dd HH:mm}");
        }
        return Truncate(sb.ToString(), 6000);
    }

    private async Task<string> ApprovalHandleProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var reqId = IntArg(root, "requestId");
        var approve = root.TryGetProperty("approve", out var av) && av.ValueKind is JsonValueKind.True or JsonValueKind.False ? av.GetBoolean() : (bool?)null;
        var comment = StrArg(root, "comment")?.Trim();
        if (!reqId.HasValue || !approve.HasValue) return "错误：需要 requestId 与 approve(true=通过/false=驳回)";
        if (!approve.Value && string.IsNullOrWhiteSpace(comment)) return "错误：驳回时请填写意见（comment）";
        if (!string.IsNullOrEmpty(comment) && comment.Length > InputLimits.ApprovalCommentMaxLength) return "错误：意见不能超过 1000 字";

        var mine = await db.ApprovalSteps.AsNoTracking()
            .Where(s => s.ApprovalRequestId == reqId.Value && s.ApproverUserId == operatorUserId
                     && s.ApprovalStatus == ApprovalStatus.Pending)
            .Select(s => new
            {
                s.ApprovalRequest.RequestNo,
                Applicant = s.ApprovalRequest.Applicant.RealName,
                Eno = s.ApprovalRequest.Applicant.EmployeeNo,
                Type = s.ApprovalRequest.ApprovalType
            })
            .FirstOrDefaultAsync(ct);
        if (mine is null) return "错误：没有找到指派给你且待处理的这张单（可能已被处理）";

        var param = JsonSerializer.Serialize(new { requestId = reqId.Value, approve = approve.Value, comment });
        var verb = approve.Value ? "通过" : "驳回";
        var summary = $"{verb}审批单 #{reqId.Value} [{mine.RequestNo}] {mine.Applicant}（{mine.Eno}）的{mine.Type.ToDisplayName()}"
                    + (string.IsNullOrEmpty(comment) ? "" : $"（意见：{comment}）");
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "approval_handle_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    /// <summary>代员工提交请假/加班/出差申请：只生成提案；真正提交（进入正常审批流程）在管理员点确认执行时才发生。</summary>
    private async Task<string> ApprovalSubmitOnBehalfProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var userId = IntArg(root, "userId");
        var type = StrArg(root, "type");
        var startS = StrArg(root, "startTime");
        var endS = StrArg(root, "endTime");
        var leaveTypeS = StrArg(root, "leaveType");
        var destination = StrArg(root, "destination")?.Trim();
        var reason = StrArg(root, "reason")?.Trim();

        if (!userId.HasValue || userId.Value <= 0) return "错误：需要有效的 userId";
        if (type is not ("leave" or "overtime" or "businesstrip")) return "错误：type 需为 leave/overtime/businesstrip";
        if (!DateTime.TryParse(startS, out var start) || !DateTime.TryParse(endS, out var end))
            return "错误：startTime/endTime 需为 yyyy-MM-dd HH:mm 格式";
        if (end <= start) return "错误：结束时间必须晚于开始时间";
        if (string.IsNullOrWhiteSpace(reason)) return "错误：reason（申请理由）必填";
        if (type == "businesstrip" && string.IsNullOrWhiteSpace(destination)) return "错误：type=businesstrip 时 destination（出差目的地）必填";

        LeaveType? leaveType = null;
        if (type == "leave")
        {
            leaveType = leaveTypeS switch
            {
                "sick" => LeaveType.SickLeave, "personal" => LeaveType.PersonalLeave, "annual" => LeaveType.AnnualLeave,
                "marriage" => LeaveType.MarriageLeave, "maternity" => LeaveType.MaternityLeave,
                "bereavement" => LeaveType.BereavementLeave, "compensatory" => LeaveType.CompensatoryLeave,
                _ => (LeaveType?)null
            };
            if (leaveType is null) return "错误：type=leave 时 leaveType 需为 sick/personal/annual/marriage/maternity/bereavement/compensatory 之一";
        }

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.AttendanceGroupId, u.IsActive, u.IsBlacklisted })
            .FirstOrDefaultAsync(ct);
        if (target is null) return "错误：目标员工不存在";
        if (!target.IsActive || target.IsBlacklisted) return "错误：该员工已停用或在黑名单中，不能代为提交申请";
        if (visibleIds is not null && (target.DepartmentId is null || !visibleIds.Contains(target.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        // 该员工所在考勤组如果配了审批人名单，代提交必须指定其中一个（跟 SubmitApprovalAsync 里
        // CreateApprovalStepsAsync 的口径一致：配了名单就必须选，不能自动退回直属上级）；不指定
        // 会在这里直接报错并给出名单，而不是让确认执行的时候才报"请选择有效的审批人"
        // （2026-09-29 审查发现 M1：以前完全没传这个参数，配了审批人名单的考勤组代提交必然失败）
        var groupApprovers = target.AttendanceGroupId.HasValue
            ? await db.AttendanceGroupApprovers.Where(a => a.AttendanceGroupId == target.AttendanceGroupId.Value)
                .Include(a => a.Approver).Where(a => a.Approver.IsActive)
                .Select(a => new { a.UserId, a.Approver.RealName, a.Approver.EmployeeNo }).ToListAsync(ct)
            : [];
        var approverUserId = IntArg(root, "approverUserId");
        if (groupApprovers.Count > 0)
        {
            if (!approverUserId.HasValue || approverUserId.Value == target.Id
                || !groupApprovers.Any(a => a.UserId == approverUserId.Value))
            {
                var options = string.Join("、", groupApprovers.Select(a => $"{a.RealName}（{a.EmployeeNo}，userId:{a.UserId}）"));
                return $"错误：该员工所在考勤组配置了审批人名单，代提交必须指定其中一位（approverUserId），可选：{options}";
            }
        }

        var param = JsonSerializer.Serialize(new
        {
            userId = target.Id, type, startTime = start.ToString("yyyy-MM-dd HH:mm"), endTime = end.ToString("yyyy-MM-dd HH:mm"),
            leaveType = leaveType.HasValue ? (int)leaveType.Value : (int?)null, destination, reason, approverUserId
        });
        var typeText = type switch { "leave" => $"请假（{LeaveTypeText(leaveType!.Value)}）", "overtime" => "加班", _ => $"出差（{destination}）" };
        var summary = $"代 {target.RealName}（{target.EmployeeNo}）提交{typeText}申请：{start:MM-dd HH:mm} ~ {end:MM-dd HH:mm}，理由：{reason}"
                    + "（提交后仍需走正常审批流程，不会直接生效）";

        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "approval_submit_on_behalf_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」才会真正提交；15 分钟内有效。";
    }

    private static string LeaveTypeText(LeaveType t) => t switch
    {
        LeaveType.SickLeave => "病假", LeaveType.PersonalLeave => "事假", LeaveType.AnnualLeave => "年假",
        LeaveType.MarriageLeave => "婚假", LeaveType.MaternityLeave => "产假", LeaveType.BereavementLeave => "丧假",
        LeaveType.CompensatoryLeave => "调休", _ => t.ToString()
    };

    // ── 公告：发布 / 撤回（提案） ───────────────────────────────────────────────

    /// <summary>读操作者是否为总部（不受限且 Admin）。</summary>
    private async Task<bool> IsHqOperatorAsync(int operatorUserId)
    {
        var op = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        return op is { Role: UserRole.Admin } && op.ScopedDepartmentId is null;
    }

    /// <summary>考勤组受众安全：组内所有在职成员的部门都必须落在操作者可见范围（无部门成员视为总部共享）。</summary>
    private async Task<(bool ok, string? err)> GroupAudienceSafeAsync(int groupId, HashSet<int>? visibleIds)
    {
        if (visibleIds is null) return (true, null);
        var memberDepts = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.AttendanceGroupId == groupId)
            .Select(u => u.DepartmentId)
            .ToListAsync();
        if (memberDepts.Any(d => !d.HasValue || !visibleIds.Contains(d.Value)))
            return (false, "该考勤组包含范围外/无部门的成员，发布会把公告发给范围外的人；请改用部门范围或联系总部");
        return (true, null);
    }

    private async Task<string> AnnouncementPublishProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var title = StrArg(root, "title")?.Trim();
        var content = StrArg(root, "content")?.Trim();
        var scope = StrArg(root, "scope");
        var scopeId = IntArg(root, "scopeId");
        if (string.IsNullOrEmpty(title)) return "错误：需要 title";
        if (title.Length > InputLimits.AnnouncementTitleMaxLength) return "错误：标题不能超过 200 字";
        if (string.IsNullOrEmpty(content)) return "错误：需要 content";
        if (content.Length > InputLimits.AnnouncementContentMaxLength) return "错误：正文不能超过 2000 字";
        if (scope is not ("all" or "department" or "attendancegroup")) return "错误：scope 应为 all/department/attendancegroup";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var isHq = await IsHqOperatorAsync(operatorUserId);

        string scopeText;
        if (scope == "all")
        {
            if (!isHq) return "错误：全公司公告只有总部能发";
            scopeText = "全公司";
        }
        else if (scope == "department")
        {
            if (!scopeId.HasValue) return "错误：department 范围需要 scopeId(部门id)";
            var deptName = await db.Departments.Where(d => d.Id == scopeId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct);
            if (deptName is null) return "错误：部门不存在";
            if (visibleIds is not null && !visibleIds.Contains(scopeId.Value)) return "错误：该部门不在你的管理范围内";
            scopeText = $"部门【{deptName}】（含下级）";
        }
        else
        {
            if (!scopeId.HasValue) return "错误：attendancegroup 范围需要 scopeId(考勤组id)";
            var g = await db.AttendanceGroups.Where(x => x.Id == scopeId.Value).Select(x => x.GroupName).FirstOrDefaultAsync(ct);
            if (g is null) return "错误：考勤组不存在";
            if (visibleIds is not null && !await GroupWritableAsync(scopeId.Value, visibleIds)) return "错误：无权给该考勤组发公告（含范围外部门/成员）";
            var (ok, verr) = await GroupAudienceSafeAsync(scopeId.Value, visibleIds);
            if (!ok) return $"错误：{verr}";
            scopeText = $"考勤组【{g}】";
        }

        var titlePreview = title.Length > 30 ? title[..30] + "…" : title;
        var param = JsonSerializer.Serialize(new { title, content, scope, scopeId });
        var summary = $"发布公告《{titlePreview}》到{scopeText}（将通知范围内所有在职员工）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "announcement_publish_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> AnnouncementWithdrawProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var annId = IntArg(doc.RootElement, "announcementId");
        if (!annId.HasValue) return "错误：需要 announcementId";

        var isHq = await IsHqOperatorAsync(operatorUserId);
        var ann = await db.Announcements.AsNoTracking()
            .Where(a => a.Id == annId.Value)
            .Select(a => new { a.Id, a.Title, a.PublisherUserId, a.IsActive })
            .FirstOrDefaultAsync();
        if (ann is null) return "错误：公告不存在";
        if (ann.PublisherUserId != operatorUserId && !isHq)
            return "错误：只能撤回自己发布的公告（总部可撤回任意公告）";

        var titlePreview = ann.Title.Length > 30 ? ann.Title[..30] + "…" : ann.Title;
        var param = JsonSerializer.Serialize(new { announcementId = ann.Id });
        var summary = $"撤下公告《{titlePreview}》";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "announcement_withdraw_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    // ── 考勤机：登记 / 变更（提案） ─────────────────────────────────────────────

    private async Task<string> DeviceRegisterProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var sn = StrArg(root, "sn")?.Trim();
        var name = StrArg(root, "name")?.Trim();
        var deptId = IntArg(root, "departmentId");
        if (string.IsNullOrEmpty(sn)) return "错误：需要 sn（序列号）";
        if (!System.Text.RegularExpressions.Regex.IsMatch(sn, @"^[A-Za-z0-9._-]{1,50}$"))
            return "错误：序列号只能包含字母/数字/点/横线/下划线（≤50）";
        if (await db.ZKDevices.AnyAsync(d => d.SN == sn)) return "错误：该序列号已登记";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        if (visibleIds is not null)
        {
            if (!deptId.HasValue) return "错误：受限管理员登记设备必须指定归属部门";
            if (!visibleIds.Contains(deptId.Value)) return "错误：归属部门不在你的管理范围内";
        }
        else if (deptId.HasValue)
        {
            var ok = await db.Departments.AnyAsync(d => d.Id == deptId.Value);
            if (!ok) return "错误：归属部门不存在";
        }

        var param = JsonSerializer.Serialize(new { sn, name, departmentId = deptId });
        var deptName = deptId.HasValue ? await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct) : null;
        var summary = $"登记考勤机 SN:{sn}" + (string.IsNullOrEmpty(name) ? "" : $"（{name}）") + (deptName is not null ? $" → 归属【{deptName}】" : "（总部共用/未归类）");
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "device_register_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> DeviceUpdateProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var deviceId = IntArg(root, "deviceId");
        var name = StrArg(root, "name")?.Trim();
        var active = root.TryGetProperty("active", out var av) && av.ValueKind is JsonValueKind.True or JsonValueKind.False ? av.GetBoolean() : (bool?)null;
        var deptParam = IntArg(root, "departmentId");   // 0 → null（总部共用）
        int? newDept = deptParam.HasValue && deptParam.Value == 0 ? null : deptParam;
        if (!deviceId.HasValue) return "错误：需要 deviceId";
        if (name is null && !active.HasValue && !deptParam.HasValue) return "错误：至少提供 name/active/departmentId 之一";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var d = await db.ZKDevices.AsNoTracking()
            .Where(x => x.Id == deviceId.Value)
            .Select(x => new { x.Id, x.SN, x.Name, x.DepartmentId })
            .FirstOrDefaultAsync(ct);
        if (d is null) return "错误：考勤机不存在";
        if (visibleIds is not null)
        {
            if (d.DepartmentId is null || !visibleIds.Contains(d.DepartmentId.Value))
                return "错误：该考勤机（未归类或其它范围）不在你的管理范围内";
            if (newDept.HasValue && !visibleIds.Contains(newDept.Value))
                return "错误：新归属部门不在你的管理范围内";
        }
        else if (newDept.HasValue && !await db.Departments.AnyAsync(x => x.Id == newDept.Value))
            return "错误：新归属部门不存在";

        var changes = new System.Collections.Generic.List<string>();
        if (name is not null) changes.Add("改别名");
        if (active.HasValue) changes.Add(active.Value ? "启用" : "停用");
        if (deptParam.HasValue)
        {
            var dn = newDept.HasValue ? await db.Departments.Where(x => x.Id == newDept.Value).Select(x => x.DeptName).FirstOrDefaultAsync(ct) : null;
            changes.Add(dn is not null ? $"归属→【{dn}】" : "归属→总部共用/未归类");
        }

        var param = JsonSerializer.Serialize(new { deviceId = d.Id, name, active, departmentId = newDept });
        var summary = $"修改考勤机 {(d.Name ?? d.SN)}（SN:{d.SN}）：{string.Join("、", changes)}";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "device_update_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    // ── 员工：普通建档 / 改资料 / 批量停启用（提案） ────────────────────────────

    /// <summary>校验直属上级：在职 且 角色为主管/班组长 且与部门一致。</summary>
    private async Task<(bool ok, string? err)> ValidateSupervisorAsync(int supervisorId, int deptId)
    {
        var sup = await db.Users.AsNoTracking()
            .Where(u => u.Id == supervisorId)
            .Select(u => new { u.IsActive, u.Role, u.DepartmentId })
            .FirstOrDefaultAsync();
        if (sup is null || !sup.IsActive || sup.Role is not (UserRole.Supervisor or UserRole.TeamLeader))
            return (false, "直属上级必须是在职的主管/班组长");
        if (sup.DepartmentId != deptId)
            return (false, "直属上级必须和目标员工在同一个部门");
        return (true, null);
    }

    private async Task<string> EmployeeCreateProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var realName = StrArg(root, "realName")?.Trim();
        var deptId   = IntArg(root, "deptId");
        var supId    = IntArg(root, "supervisorId");
        var phone    = StrArg(root, "phone")?.Trim();
        var employeeNo = StrArg(root, "employeeNo")?.Trim();
        var position = StrArg(root, "position")?.Trim();
        var contract = StrArg(root, "contractCompany")?.Trim();
        var hireS    = StrArg(root, "hireDate")?.Trim();

        if (string.IsNullOrEmpty(realName)) return "错误：需要 realName";
        if (!deptId.HasValue || !supId.HasValue) return "错误：需要 deptId 与 supervisorId（用 user_search 查 userId）";
        if (string.IsNullOrEmpty(phone) || !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^1[3-9]\d{9}$"))
            return "错误：手机号格式不正确（11 位大陆手机号）";
        if (!string.IsNullOrEmpty(employeeNo) && !System.Text.RegularExpressions.Regex.IsMatch(employeeNo, @"^[A-Za-z0-9_-]{1,50}$"))
            return "错误：工号只能包含字母/数字/下划线/短横线";
        if (!string.IsNullOrEmpty(hireS) && !DateOnly.TryParse(hireS, out _))
            return "错误：hireDate 格式应为 yyyy-MM-dd";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var deptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct);
        if (deptName is null) return "错误：归属部门不存在";
        if (visibleIds is not null && !visibleIds.Contains(deptId.Value)) return "错误：归属部门不在你的管理范围内";
        if (supId.Value == operatorUserId) return "错误：直属上级不能是自己";
        var (vok, verr) = await ValidateSupervisorAsync(supId.Value, deptId.Value);
        if (!vok) return $"错误：{verr}";

        var param = JsonSerializer.Serialize(new
        {
            realName, deptId = deptId.Value, supervisorId = supId.Value, phone, employeeNo, position,
            contractCompany = contract, hireDate = string.IsNullOrEmpty(hireS) ? null : hireS
        });
        var summary = $"新建员工 {realName}：部门【{deptName}】" + (string.IsNullOrEmpty(employeeNo) ? "（工号自动生成）" : $"（工号 {employeeNo}）") + "，初始密码 123456";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "employee_create_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> EmployeeUpdateProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var userId = IntArg(root, "userId");
        if (!userId.HasValue || userId.Value <= 0) return "错误：需要 userId";

        var realName = StrArg(root, "realName")?.Trim();
        var deptId   = IntArg(root, "deptId");
        var supId    = IntArg(root, "supervisorId");
        var phone    = StrArg(root, "phone")?.Trim();
        var position = StrArg(root, "position")?.Trim();
        var contract = StrArg(root, "contractCompany")?.Trim();
        var hireS    = StrArg(root, "hireDate")?.Trim();
        if (realName is null && !deptId.HasValue && !supId.HasValue && phone is null && position is null && contract is null && hireS is null)
            return "错误：至少提供一个要修改的字段";
        if (phone is not null && !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^1[3-9]\d{9}$"))
            return "错误：手机号格式不正确";
        if (!string.IsNullOrEmpty(hireS) && !DateOnly.TryParse(hireS, out _))
            return "错误：hireDate 格式应为 yyyy-MM-dd";
        if (userId.Value == operatorUserId && deptId.HasValue)
            return "错误：不能修改自己的部门归属，请在员工管理页处理";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (target is null) return "错误：目标员工不存在";
        if (visibleIds is not null && (target.DepartmentId is null || !visibleIds.Contains(target.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        var updateOp = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (updateOp is null || !Middlewares.CurrentUser.CanManageAccountCore(updateOp.Role, updateOp.ScopedDepartmentId, target.Role, target.ScopedDepartmentId))
            return "错误：无权操作该账号（角色层级限制）";

        var finalDeptId = deptId ?? target.DepartmentId;
        string? newDeptName = null;
        if (deptId.HasValue)
        {
            if (visibleIds is not null && !visibleIds.Contains(deptId.Value)) return "错误：新部门不在你的管理范围内";
            newDeptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct);
            if (newDeptName is null) return "错误：新部门不存在";
        }
        if (supId.HasValue)
        {
            if (supId.Value == target.Id) return "错误：直属上级不能是自己";
            if (!finalDeptId.HasValue) return "错误：目标员工未分部门，不能设置直属上级";
            var (vok, verr) = await ValidateSupervisorAsync(supId.Value, finalDeptId.Value);
            if (!vok) return $"错误：{verr}";
        }

        var changes = new System.Collections.Generic.List<string>();
        if (realName is not null) changes.Add($"改名 {target.RealName}→{realName}");
        if (deptId.HasValue) changes.Add($"调部门→【{newDeptName}】");
        if (supId.HasValue) changes.Add("更换直属上级");
        if (phone is not null) changes.Add("改手机号");
        if (position is not null) changes.Add("改岗位");
        if (contract is not null) changes.Add("改合同公司");
        if (hireS is not null) changes.Add("改入职日期");

        var param = JsonSerializer.Serialize(new
        {
            userId = target.Id, realName, deptId, supervisorId = supId, phone, position,
            contractCompany = contract, hireDate = string.IsNullOrEmpty(hireS) ? null : hireS
        });
        var summary = $"修改员工 {target.RealName}（{target.EmployeeNo}）：{string.Join("、", changes)}";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "employee_update_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> EmployeeBatchToggleProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root   = doc.RootElement;
        var action = StrArg(root, "action");
        var rawIds = new System.Collections.Generic.List<int>();
        if (root.TryGetProperty("userIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number) rawIds.Add(v.GetInt32());
        rawIds = rawIds.Distinct().ToList();
        if (action is not ("deactivate" or "activate")) return "错误：需要 action（deactivate/activate）";
        if (rawIds.Count == 0) return "错误：userIds 不能为空";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        if (rawIds.Contains(operatorUserId)) return "错误：列表中包含你自己，请移除后重试";

        var users = await db.Users.AsNoTracking()
            .Where(u => rawIds.Contains(u.Id))
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.IsActive, u.IsBlacklisted, u.Role, u.ScopedDepartmentId })
            .ToListAsync(ct);
        if (users.Count != rawIds.Count) return "错误：部分目标员工不存在";
        var oob = users.Where(u => visibleIds is not null && (u.DepartmentId is null || !visibleIds.Contains(u.DepartmentId.Value)))
            .Select(u => u.EmployeeNo).ToList();
        if (oob.Count > 0) return $"错误：以下员工不在你的管理范围（{string.Join("、", oob.Take(5))}）";
        var bl = users.Where(u => u.IsBlacklisted).Select(u => u.EmployeeNo).ToList();
        if (bl.Count > 0) return $"错误：黑名单员工请单独处理（{string.Join("、", bl.Take(5))}）";

        // 角色层级：管不到的账号（比如文员提交里混了一个管理员）静默剔除、不中断整批——跟确认执行时
        // UserService.SetActiveBatchAsync 同一套"批量语义"（2026-09-29 第 12 轮审查发现）
        var batchOp = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (batchOp is null) return "错误：操作者账号不存在";
        var unmanageable = users.Where(u => !Middlewares.CurrentUser.CanManageAccountCore(batchOp.Role, batchOp.ScopedDepartmentId, u.Role, u.ScopedDepartmentId))
            .Select(u => u.EmployeeNo).ToList();
        users = users.Where(u => Middlewares.CurrentUser.CanManageAccountCore(batchOp.Role, batchOp.ScopedDepartmentId, u.Role, u.ScopedDepartmentId)).ToList();

        var filtered = action == "deactivate"
            ? users.Where(u => u.IsActive).Select(u => u.Id).ToList()
            : users.Where(u => !u.IsActive).Select(u => u.Id).ToList();
        if (filtered.Count == 0) return "错误：这些员工已处于目标状态，或都无权操作，无需操作";

        var param = JsonSerializer.Serialize(new { userIds = filtered, action });
        var verb = action == "deactivate" ? "批量停用" : "批量启用";
        var skipHint = unmanageable.Count > 0 ? $"，已跳过 {unmanageable.Count} 个无权操作的账号（{string.Join("、", unmanageable.Take(5))}）" : "";
        var summary = $"{verb} {filtered.Count} 名员工（提交 {rawIds.Count} 人，已剔除无需操作的{skipHint}）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "employee_batch_toggle_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    /// <summary>考勤组写权限（AGENT 采用"关联部门全部在范围内"口径，比页面 ANY 更严）：不受限=true；受限且零部门组=false。</summary>
    private async Task<bool> GroupWritableAsync(int groupId, HashSet<int>? visibleIds)
    {
        if (visibleIds is null) return true;
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return false;
        return deptIds.All(visibleIds.Contains);
    }
}
