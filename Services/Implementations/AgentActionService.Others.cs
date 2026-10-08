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

/// <summary>审批处理、公告发布/撤回、考勤机登记/变更的执行（<see cref="AgentActionService"/> 的一部分）。</summary>
public partial class AgentActionService
{
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
                // 提案阶段已经校验过假别，这里是纵深防御：不认识的数值直接拒绝，不能强转后让非法假别落库
                if (leaveTypeI.HasValue && !Enum.IsDefined(typeof(LeaveType), leaveTypeI.Value))
                    return (false, "请假类型不合法");
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
        if (title.Length > InputLimits.AnnouncementTitleMaxLength || content.Length > InputLimits.AnnouncementContentMaxLength) return (false, "标题≤200字、正文≤2000字");

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
            CreatedAt    = clock.LocalNow()
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
