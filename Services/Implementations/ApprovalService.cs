using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 审批服务：审批单的提交、多级流转、撤销、查询。
/// 审批通过后会联动考勤服务回写考勤记录，并在各环节发站内通知。
/// </summary>
public class ApprovalService(AttendanceDbContext db, IAttendanceService attendanceService)
    : IApprovalService
{
    /// <summary>
    /// 提交申请：算请假/加班时长 → 生成申请单(带单号) → 建审批节点(员工自选审批人/直属上级/兜底管理员) → 通知审批人。
    /// </summary>
    public async Task<ApprovalRequest> SubmitApprovalAsync(int applicantUserId, SubmitApprovalDto dto)
    {
        var user = await db.Users.FindAsync(applicantUserId)
            ?? throw new KeyNotFoundException("用户不存在");

        // 如果填了起止时间，就算出请假/加班/出差的时长
        decimal? leaveDuration  = dto.LeaveStartTime.HasValue && dto.LeaveEndTime.HasValue
            ? (decimal)(dto.LeaveEndTime.Value - dto.LeaveStartTime.Value).TotalHours : null;
        decimal? overtimeDuration = dto.OvertimeStartTime.HasValue && dto.OvertimeEndTime.HasValue
            ? (decimal)(dto.OvertimeEndTime.Value - dto.OvertimeStartTime.Value).TotalHours : null;
        decimal? businessTripDuration = dto.BusinessTripStartTime.HasValue && dto.BusinessTripEndTime.HasValue
            ? (decimal)(dto.BusinessTripEndTime.Value - dto.BusinessTripStartTime.Value).TotalDays : null;

        // 组装一张申请单
        var request = new ApprovalRequest
        {
            RequestNo          = await GenerateRequestNoAsync(dto.ApprovalType),   // 生成单号
            ApplicantUserId    = applicantUserId,
            ApprovalType       = dto.ApprovalType,
            ApprovalStatus     = ApprovalStatus.Pending,
            PunchDate          = dto.PunchDate,
            PunchType          = dto.PunchType,
            PunchTime          = dto.PunchTime,
            LeaveType          = dto.LeaveType,
            LeaveStartTime     = dto.LeaveStartTime,
            LeaveEndTime       = dto.LeaveEndTime,
            LeaveDurationHours = leaveDuration,
            OvertimeStartTime  = dto.OvertimeStartTime,
            OvertimeEndTime    = dto.OvertimeEndTime,
            OvertimeDurationHours = overtimeDuration,
            BusinessTripStartTime    = dto.BusinessTripStartTime,
            BusinessTripEndTime      = dto.BusinessTripEndTime,
            BusinessTripDurationDays = businessTripDuration,
            BusinessTripDestination  = dto.BusinessTripDestination,
            Reason             = dto.Reason,
            // 附件列表转成 JSON 文本存进一个字段
            AttachmentUrls     = dto.AttachmentUrls.Count > 0
                ? JsonSerializer.Serialize(dto.AttachmentUrls) : null,
            SubmittedAt        = DateTime.Now,
            UpdatedAt          = DateTime.Now
        };

        db.ApprovalRequests.Add(request);
        await db.SaveChangesAsync();   // 先存单子，拿到它的 Id

        await CreateApprovalStepsAsync(request, user, dto.ApproverUserId);   // 建审批节点
        await NotifyApproversAsync(request);             // 通知第一个审批人
        return request;
    }

    /// <summary>
    /// 审批人处理当前待办：
    /// 驳回 → 整单驳回；通过且后面还有节点 → 流转下一节点；
    /// 通过且是最后一节点 → 整单通过并回写考勤。处理完通知申请人。
    /// </summary>
    public async Task<bool> HandleApprovalAsync(int approverUserId, HandleApprovalDto dto)
    {
        // 只能处理「属于本人、且还在待审批」的那个节点
        var step = await db.ApprovalSteps
            .Include(s => s.ApprovalRequest)
            .FirstOrDefaultAsync(s =>
                s.ApprovalRequestId == dto.ApprovalRequestId &&
                s.ApproverUserId    == approverUserId &&
                s.ApprovalStatus    == ApprovalStatus.Pending);
        if (step is null) return false;   // 不是你的待办，拒绝

        // 逐层递进的顺序闸：只要前面还有更靠前的环节没审批完，就不轮到当前审批人，禁止越级
        var earlierPending = await db.ApprovalSteps.AnyAsync(s =>
            s.ApprovalRequestId == dto.ApprovalRequestId &&
            s.StepOrder         < step.StepOrder &&
            s.ApprovalStatus    == ApprovalStatus.Pending);
        if (earlierPending) return false;   // 前一级还没审，当前这级不能先审

        // 记录这一级的处理结果
        step.ApprovalStatus = dto.IsApproved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        step.Comment        = dto.Comment;
        step.HandledAt      = DateTime.Now;

        var request = step.ApprovalRequest;

        if (!dto.IsApproved)
        {
            request.ApprovalStatus = ApprovalStatus.Rejected;   // 只要有人驳回，整单驳回

            // 后面还没轮到的环节直接作废，避免它们一直挂在别人的“待我审批”里
            var laterSteps = await db.ApprovalSteps
                .Where(s => s.ApprovalRequestId == dto.ApprovalRequestId
                         && s.StepOrder > step.StepOrder
                         && s.ApprovalStatus == ApprovalStatus.Pending)
                .ToListAsync();
            foreach (var ls in laterSteps) ls.ApprovalStatus = ApprovalStatus.Cancelled;
        }
        else
        {
            // 找当前节点之后还在等待的下一节点
            var nextStep = await db.ApprovalSteps
                .Where(s => s.ApprovalRequestId == dto.ApprovalRequestId
                         && s.StepOrder > step.StepOrder
                         && s.ApprovalStatus == ApprovalStatus.Pending)
                .OrderBy(s => s.StepOrder)
                .FirstOrDefaultAsync();

            if (nextStep is null)
            {
                // 没有下一节点了 → 整单通过，并回写考勤（补卡补时间/请假置请假）
                request.ApprovalStatus = ApprovalStatus.Approved;
                await attendanceService.UpdateAttendanceAfterApprovalAsync(request.Id);
            }
            else
            {
                // 还有下一节点 → 状态改“审批中”，通知下一个审批人
                request.ApprovalStatus = ApprovalStatus.InProgress;
                await NotifyNextApproverAsync(request, nextStep);
            }
        }

        request.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await NotifyApplicantAsync(request);   // 通知申请人结果
        return true;
    }

    /// <summary>申请人撤销自己的申请（只有“待审批”状态能撤）。</summary>
    public async Task<bool> CancelApprovalAsync(int userId, int approvalRequestId)
    {
        var request = await db.ApprovalRequests
            .FirstOrDefaultAsync(a => a.Id == approvalRequestId && a.ApplicantUserId == userId);
        if (request is null || request.ApprovalStatus != ApprovalStatus.Pending)
            return false;
        request.ApprovalStatus = ApprovalStatus.Cancelled;
        request.UpdatedAt      = DateTime.Now;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>分页查询审批记录（多条件过滤）。</summary>
    public async Task<(List<ApprovalRequestDto> Items, int Total)> QueryApprovalsAsync(ApprovalQueryDto q)
    {
        var query = db.ApprovalRequests
            .Include(a => a.Applicant).ThenInclude(u => u.Department)
            .Include(a => a.ApprovalSteps).ThenInclude(s => s.Approver)
            .AsQueryable();

        if (q.ApplicantUserId.HasValue) query = query.Where(a => a.ApplicantUserId == q.ApplicantUserId.Value);
        if (q.ApprovalType.HasValue)    query = query.Where(a => a.ApprovalType    == q.ApprovalType.Value);
        if (q.ApprovalStatus.HasValue)  query = query.Where(a => a.ApprovalStatus  == q.ApprovalStatus.Value);
        if (q.StartDate.HasValue)       query = query.Where(a => a.SubmittedAt     >= q.StartDate.Value);
        if (q.EndDate.HasValue)         query = query.Where(a => a.SubmittedAt     <= q.EndDate.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.SubmittedAt)
            .Skip((q.PageIndex - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync();

        return (items.Select(ToDto).ToList(), total);
    }

    /// <summary>查申请详情（带权限校验：只有申请人本人/该单审批人/管理员文员能看）。</summary>
    public async Task<ApprovalRequestDto?> GetApprovalDetailAsync(int id, int requesterUserId, bool isManager)
    {
        var request = await db.ApprovalRequests
            .Include(a => a.Applicant).ThenInclude(u => u.Department)
            .Include(a => a.ApprovalSteps).ThenInclude(s => s.Approver)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (request is null) return null;

        // 允许查看的三种人：管理员/文员、申请人本人、这张单的某个审批人
        bool allowed = isManager
                       || request.ApplicantUserId == requesterUserId
                       || request.ApprovalSteps.Any(s => s.ApproverUserId == requesterUserId);
        return allowed ? ToDto(request) : null;   // 没权限就当查不到
    }

    /// <summary>查“待我审批”的申请列表（只返回当前正轮到我这一级的单，实现逐层递进）。</summary>
    public async Task<List<ApprovalRequestDto>> GetPendingForApproverAsync(int approverUserId)
    {
        // 只看“未结束(待审批/审批中)”且我有待办节点的申请（已通过/驳回/撤销的不再出现）
        var candidates = await db.ApprovalRequests
            .Include(a => a.Applicant).ThenInclude(u => u.Department)
            .Include(a => a.ApprovalSteps).ThenInclude(s => s.Approver)
            .Where(a => (a.ApprovalStatus == ApprovalStatus.Pending || a.ApprovalStatus == ApprovalStatus.InProgress)
                     && a.ApprovalSteps.Any(s => s.ApproverUserId == approverUserId
                                              && s.ApprovalStatus == ApprovalStatus.Pending))
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync();

        // 逐层递进：当前应处理的是“最小 StepOrder 的待审批节点”，只有它的审批人才算轮到
        return candidates
            .Where(a =>
            {
                var activeOrder = a.ApprovalSteps
                    .Where(s => s.ApprovalStatus == ApprovalStatus.Pending)
                    .Min(s => s.StepOrder);
                return a.ApprovalSteps.Any(s => s.StepOrder == activeOrder
                                             && s.ApproverUserId == approverUserId
                                             && s.ApprovalStatus == ApprovalStatus.Pending);
            })
            .Select(ToDto).ToList();
    }

    /// <summary>查“我提交的”申请列表（可按状态过滤）。</summary>
    public async Task<List<ApprovalRequestDto>> GetMyApprovalsAsync(
        int userId, ApprovalStatus? status = null)
    {
        var q = db.ApprovalRequests
            .Include(a => a.Applicant)
            .Include(a => a.ApprovalSteps).ThenInclude(s => s.Approver)
            .Where(a => a.ApplicantUserId == userId);

        if (status.HasValue) q = q.Where(a => a.ApprovalStatus == status.Value);

        return (await q.OrderByDescending(a => a.SubmittedAt).ToListAsync())
            .Select(ToDto).ToList();
    }

    /// <summary>
    /// 查某员工提交申请时可选的审批人名单（取自其所在考勤组配置的审批人）。
    /// </summary>
    public async Task<List<ApproverOptionDto>> GetAvailableApproversAsync(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user?.AttendanceGroupId is null) return [];   // 没有考勤组，就没有名单可选

        return await db.AttendanceGroupApprovers
            .Where(a => a.AttendanceGroupId == user.AttendanceGroupId)
            .Include(a => a.Approver)
            .OrderBy(a => a.Approver.RealName)
            .Select(a => new ApproverOptionDto
            {
                UserId   = a.UserId,
                RealName = a.Approver.RealName,
                Position = a.Approver.Position
            })
            .ToListAsync();
    }

    /// <summary>生成申请单号：前缀(BK补卡/QJ请假/JB加班) + 日期 + 当天第几单。</summary>
    public async Task<string> GenerateRequestNoAsync(ApprovalType type)
    {
        var prefix = type switch
        {
            ApprovalType.PunchReplenishment => "BK",
            ApprovalType.Leave              => "QJ",
            ApprovalType.Overtime           => "JB",
            ApprovalType.BusinessTrip       => "CC",
            _                               => "AP"
        };
        var date  = DateTime.Now.ToString("yyyyMMdd");
        var count = await db.ApprovalRequests.CountAsync(a => a.SubmittedAt.Date == DateTime.Today) + 1;
        return $"{prefix}{date}{count:D4}";   // 如 QJ202606250001
    }

    // ── 私有方法 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 为申请创建审批节点（现在只会有一个节点，一步审完）：
    /// 考勤组配了审批人名单 → 必须是员工自己从名单里选的那个人；
    /// 组里没配名单 → 回退“直属上级”；连上级也没有 → 兜底指派一名管理员/文员，避免申请永远没人审。
    /// </summary>
    private async Task CreateApprovalStepsAsync(ApprovalRequest request, User applicant, int? selectedApproverUserId)
    {
        var groupApproverIds = await db.AttendanceGroupApprovers
            .Where(a => a.AttendanceGroupId == applicant.AttendanceGroupId)
            .Select(a => a.UserId)
            .ToListAsync();

        int? approverId;
        if (groupApproverIds.Count > 0)
        {
            // 组里配了审批人名单：员工必须选中名单里的一个人，不能自己瞎填/绕过名单
            if (selectedApproverUserId is null || !groupApproverIds.Contains(selectedApproverUserId.Value))
                throw new InvalidOperationException("请选择有效的审批人");
            approverId = selectedApproverUserId;
        }
        else
        {
            // 组里没配名单：退回直属上级；没上级就兜底找个管理员/文员
            approverId = applicant.SupervisorUserId ?? await ResolveFallbackApproverAsync(applicant);
        }

        if (approverId.HasValue)
            db.ApprovalSteps.Add(new ApprovalStep
            {
                ApprovalRequestId = request.Id,
                ApproverUserId    = approverId.Value,
                StepOrder         = 1,
                ApprovalStatus    = ApprovalStatus.Pending,
                CreatedAt         = DateTime.Now
            });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 兜底审批人：没审批流也没上级时，指派一名在职管理员/文员来审。
    /// 优先同考勤组的，其次随便一个，并排除申请人自己，避免“无人可审”。
    /// </summary>
    private async Task<int?> ResolveFallbackApproverAsync(User applicant)
    {
        var managers = await db.Users
            .Where(u => u.IsActive
                     && (u.Role == UserRole.Admin || u.Role == UserRole.Clerk)
                     && u.Id != applicant.Id)
            .Select(u => new { u.Id, u.AttendanceGroupId })
            .ToListAsync();
        if (managers.Count == 0) return null;
        var sameGroup = managers.FirstOrDefault(m => m.AttendanceGroupId == applicant.AttendanceGroupId);
        return (sameGroup ?? managers[0]).Id;   // 优先同组，否则取第一个
    }

    /// <summary>新申请提交后，通知第一个审批人。</summary>
    private async Task NotifyApproversAsync(ApprovalRequest request)
    {
        var firstStep = await db.ApprovalSteps
            .Where(s => s.ApprovalRequestId == request.Id)
            .OrderBy(s => s.StepOrder)
            .FirstOrDefaultAsync();
        if (firstStep is null) return;

        var applicant = await db.Users.FindAsync(request.ApplicantUserId);
        await AddNotificationAsync(firstStep.ApproverUserId, "您有新的待审批申请",
            $"{applicant?.RealName} 提交了{request.ApprovalType.ToDisplayName()}申请（{request.RequestNo}），请及时处理",
            "ApprovalPending", request.Id);
    }

    /// <summary>多级审批：上一级通过后，通知下一级审批人。</summary>
    private async Task NotifyNextApproverAsync(ApprovalRequest request, ApprovalStep nextStep)
    {
        var applicant = await db.Users.FindAsync(request.ApplicantUserId);
        await AddNotificationAsync(nextStep.ApproverUserId, "审批流转通知",
            $"{applicant?.RealName} 的{request.ApprovalType.ToDisplayName()}申请（{request.RequestNo}）已流转至您，请处理",
            "ApprovalPending", request.Id);
    }

    /// <summary>审批结束（通过/驳回）后，通知申请人。</summary>
    private async Task NotifyApplicantAsync(ApprovalRequest request)
    {
        var statusText = request.ApprovalStatus == ApprovalStatus.Approved ? "已通过" : "已驳回";
        await AddNotificationAsync(request.ApplicantUserId, $"审批{statusText}",
            $"您的{request.ApprovalType.ToDisplayName()}申请（{request.RequestNo}）{statusText}",
            "ApprovalResult", request.Id);
    }

    /// <summary>写一条站内通知并立即保存（上面三个通知方法都调它）。</summary>
    private async Task AddNotificationAsync(int userId, string title, string content,
        string type, int relatedId)
    {
        db.Notifications.Add(new Notification
        {
            UserId           = userId,
            Title            = title,
            Content          = content,
            NotificationType = type,
            RelatedId        = relatedId,
            CreatedAt        = DateTime.Now
        });
        await db.SaveChangesAsync();
    }

    /// <summary>把“审批申请”实体转成给页面用的展示对象(DTO)，含附件解析和各级节点。</summary>
    private static ApprovalRequestDto ToDto(ApprovalRequest a)
    {
        // 附件是以 JSON 文本存的，这里解析回字符串列表
        List<string> attachments = [];
        if (!string.IsNullOrEmpty(a.AttachmentUrls))
        {
            try { attachments = JsonSerializer.Deserialize<List<string>>(a.AttachmentUrls) ?? []; }
            catch { /* 解析失败就当没附件，忽略 */ }
        }

        return new ApprovalRequestDto
        {
            Id                    = a.Id,
            RequestNo             = a.RequestNo,
            ApplicantName         = a.Applicant.RealName,
            ApplicantEmployeeNo   = a.Applicant.EmployeeNo,
            DeptName              = a.Applicant.Department?.DeptName,
            ApprovalType          = a.ApprovalType,
            ApprovalTypeText      = a.ApprovalType.ToDisplayName(),
            ApprovalStatus        = a.ApprovalStatus,
            ApprovalStatusText    = StatusText(a.ApprovalStatus),
            ApprovalStatusCss     = StatusCss(a.ApprovalStatus),
            PunchDate             = a.PunchDate,
            PunchType             = a.PunchType,
            PunchTime             = a.PunchTime,
            LeaveType             = a.LeaveType,
            LeaveTypeText         = a.LeaveType.HasValue ? LeaveTypeName(a.LeaveType.Value) : null,
            LeaveStartTime        = a.LeaveStartTime,
            LeaveEndTime          = a.LeaveEndTime,
            LeaveDurationHours    = a.LeaveDurationHours,
            OvertimeStartTime     = a.OvertimeStartTime,
            OvertimeEndTime       = a.OvertimeEndTime,
            OvertimeDurationHours = a.OvertimeDurationHours,
            BusinessTripStartTime    = a.BusinessTripStartTime,
            BusinessTripEndTime      = a.BusinessTripEndTime,
            BusinessTripDurationDays = a.BusinessTripDurationDays,
            BusinessTripDestination  = a.BusinessTripDestination,
            Reason                = a.Reason,
            AttachmentUrls        = attachments,
            SubmittedAt           = a.SubmittedAt,
            // 各级审批节点，按顺序展开
            Steps = a.ApprovalSteps.OrderBy(s => s.StepOrder).Select(s => new ApprovalStepDto
            {
                StepOrder    = s.StepOrder,
                ApproverName = s.Approver.RealName,
                Status       = s.ApprovalStatus,
                StatusText   = StatusText(s.ApprovalStatus),
                Comment      = s.Comment,
                HandledAt    = s.HandledAt
            }).ToList()
        };
    }

    /// <summary>审批状态翻译成中文。</summary>
    private static string StatusText(ApprovalStatus s) => s switch
    {
        ApprovalStatus.Pending    => "待审批",
        ApprovalStatus.InProgress => "审批中",
        ApprovalStatus.Approved   => "已通过",
        ApprovalStatus.Rejected   => "已驳回",
        ApprovalStatus.Cancelled  => "已撤销",
        _                         => "未知"
    };

    /// <summary>审批状态对应的前端颜色样式。</summary>
    private static string StatusCss(ApprovalStatus s) => s switch
    {
        ApprovalStatus.Pending    => "f-color-orange",
        ApprovalStatus.InProgress => "f-color-blue",
        ApprovalStatus.Approved   => "f-color-green",
        ApprovalStatus.Rejected   => "f-color-red",
        ApprovalStatus.Cancelled  => "f-color-gray",
        _                         => ""
    };

    /// <summary>请假类型翻译成中文。</summary>
    private static string LeaveTypeName(LeaveType t) => t switch
    {
        LeaveType.PersonalLeave     => "事假",
        LeaveType.SickLeave         => "病假",
        LeaveType.AnnualLeave       => "年假",
        LeaveType.MarriageLeave     => "婚假",
        LeaveType.MaternityLeave    => "产假",
        LeaveType.BereavementLeave  => "丧假",
        LeaveType.CompensatoryLeave => "调休",
        _                           => "其他"
    };
}
