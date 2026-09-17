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

        // 服务端校验：页面上虽然已经有相应的输入限制，但直接调接口能绕开页面校验——
        // 起止时间颠倒/缺关键字段这种非法申请，之前能照常建单、审批通过，只是回写考勤时
        // 因为区间是"负的"循环一次都不会跑，单子显示"已通过"但考勤记录完全没变化，
        // 相当于一次静默失败，很难排查。这里在建单之前先按类型把该有的字段和先后顺序卡一遍。
        // 申请原因/出差目的地长度上限——跟页面上的限制保持一致，这里是权威兜底（直接调接口能绕开页面）
        if (string.IsNullOrWhiteSpace(dto.Reason))
            throw new InvalidOperationException("请填写申请原因");
        if (dto.Reason.Trim().Length > 1000)
            throw new InvalidOperationException("申请理由不能超过 1000 个字");
        if (!string.IsNullOrWhiteSpace(dto.BusinessTripDestination) && dto.BusinessTripDestination.Trim().Length > 200)
            throw new InvalidOperationException("出差目的地不能超过 200 个字");

        switch (dto.ApprovalType)
        {
            case ApprovalType.PunchReplenishment:
                if (dto.PunchDate is null || dto.PunchType is null || dto.PunchTime is null)
                    throw new InvalidOperationException("请填写完整的补卡日期、类型和时间");
                if (dto.PunchDate.Value > DateOnly.FromDateTime(DateTime.Today))
                    throw new InvalidOperationException("补卡日期不能晚于今天");
                break;
            case ApprovalType.Leave:
                if (dto.LeaveStartTime is null || dto.LeaveEndTime is null)
                    throw new InvalidOperationException("请填写请假的起止时间");
                if (dto.LeaveStartTime < DateTime.Now.AddHours(-24))
                    throw new InvalidOperationException("请假开始时间最早只能选到现在往前推24小时以内");
                if (dto.LeaveEndTime <= dto.LeaveStartTime)
                    throw new InvalidOperationException("请假结束时间必须晚于开始时间");
                break;
            case ApprovalType.Overtime:
                if (dto.OvertimeStartTime is null || dto.OvertimeEndTime is null)
                    throw new InvalidOperationException("请填写加班的起止时间");
                if (DateOnly.FromDateTime(dto.OvertimeStartTime.Value) != DateOnly.FromDateTime(DateTime.Today))
                    throw new InvalidOperationException("加班申请必须是当天的加班，请在当天24点前提交当日申请");
                if (dto.OvertimeEndTime <= dto.OvertimeStartTime)
                    throw new InvalidOperationException("加班结束时间必须晚于开始时间");
                break;
            case ApprovalType.BusinessTrip:
                if (dto.BusinessTripStartTime is null || dto.BusinessTripEndTime is null)
                    throw new InvalidOperationException("请填写出差的起止时间");
                if (dto.BusinessTripStartTime < DateTime.Now)
                    throw new InvalidOperationException("出差开始时间不能早于现在");
                if (dto.BusinessTripEndTime < dto.BusinessTripStartTime)
                    throw new InvalidOperationException("出差结束时间不能早于开始时间");
                break;
        }

        // 请假时长：跟"实际工时"用同一套算法（净时长超过 6/9 小时才扣一次午休/晚餐，不是跨度多长都原样算），
        // 这样"请一整天假"和"正常上一整天班"算出来的小时数口径一致，不会因为没扣午休比标准工时凭空多 1 小时。
        decimal? leaveDuration = null;
        if (dto.LeaveStartTime.HasValue && dto.LeaveEndTime.HasValue)
        {
            var group = user.AttendanceGroupId.HasValue
                ? await db.AttendanceGroups.FindAsync(user.AttendanceGroupId.Value) : null;
            leaveDuration = AttendanceService.ComputeWorkHours(
                dto.LeaveStartTime.Value, dto.LeaveEndTime.Value,
                group?.LunchBreakMinutes ?? 60, group?.DinnerBreakMinutes ?? 30);
        }
        decimal? overtimeDuration = dto.OvertimeStartTime.HasValue && dto.OvertimeEndTime.HasValue
            ? (decimal)(dto.OvertimeEndTime.Value - dto.OvertimeStartTime.Value).TotalHours : null;
        decimal? businessTripDuration = dto.BusinessTripStartTime.HasValue && dto.BusinessTripEndTime.HasValue
            ? (decimal)(dto.BusinessTripEndTime.Value - dto.BusinessTripStartTime.Value).TotalDays : null;

        // 组装一张申请单。单号（RequestNo）是"当天第几单"数出来的，两个人几乎同时提交、
        // 都数到同一个"第几单"的话，单号会撞上数据库的唯一索引导致存不进去——这种情况概率很低，
        // 但请假高峰期（比如放假前）不是不可能发生，所以套一层重试：撞了就重新数一次单号再试。
        ApprovalRequest? request = null;
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            request = new ApprovalRequest
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
            try
            {
                await db.SaveChangesAsync();   // 先存单子，拿到它的 Id
                break;   // 存成功，跳出重试
            }
            catch (DbUpdateException) when (attempt < maxAttempts)
            {
                db.ChangeTracker.Clear();   // 丢弃这次没存成功的单号冲突记录，下一轮重新数一次单号
            }
        }

        await CreateApprovalStepsAsync(request!, user, dto.ApproverUserId);   // 建审批节点
        await NotifyApproversAsync(request!);             // 通知第一个审批人
        return request!;
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
            .Include(s => s.ApprovalRequest).ThenInclude(r => r.Applicant)
            .FirstOrDefaultAsync(s =>
                s.ApprovalRequestId == dto.ApprovalRequestId &&
                s.ApproverUserId    == approverUserId &&
                s.ApprovalStatus    == ApprovalStatus.Pending);
        if (step is null) return false;   // 不是你的待办，拒绝

        // 申请人调岗后原审批节点不会自动失效，这里复核一遍当前范围，不再管得到就当"不是你的待办"
        if (!await ApproverCoversApplicantAsync(approverUserId, step.ApprovalRequest.Applicant?.DepartmentId))
            return false;

        // 逐层递进的顺序闸：只要前面还有更靠前的环节没审批完，就不轮到当前审批人，禁止越级
        var earlierPending = await db.ApprovalSteps.AnyAsync(s =>
            s.ApprovalRequestId == dto.ApprovalRequestId &&
            s.StepOrder         < step.StepOrder &&
            s.ApprovalStatus    == ApprovalStatus.Pending);
        if (earlierPending) return false;   // 前一级还没审，当前这级不能先审

        // 从抢占节点、判断整单状态到回写考勤，整个过程放进一个事务：万一半路失败，或者跟申请人
        // 几乎同一时刻点的"撤销"（CancelApprovalAsync）撞车，要么全部生效、要么全部回滚，
        // 不会出现节点状态、整单状态、考勤数据三者中只改了一部分的半成品结果。
        await using var transaction = await db.Database.BeginTransactionAsync();

        // 原子"认领"这个节点：条件里带 ApprovalStatus == Pending，只有还是待审批状态才能抢到。
        // 抢不到（返回 0 行）说明这个节点已经被处理过了——两次几乎同时的点击/请求撞上了
        // （不加这一步的话，两边都会通过上面的检查、都真的把后面的审批逻辑跑一遍，比如加班时长
        // 被累加两次）。这里用 ExecuteUpdateAsync 直接在数据库层面做条件更新，
        // 不经过内存里的 change tracker，天然是原子的，不会有"先查后改"之间的竞态窗口。
        var claimed = await db.ApprovalSteps
            .Where(s => s.Id == step.Id && s.ApprovalStatus == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ApprovalStatus, dto.IsApproved ? ApprovalStatus.Approved : ApprovalStatus.Rejected)
                .SetProperty(x => x.Comment, dto.Comment)
                .SetProperty(x => x.HandledAt, DateTime.Now));
        if (claimed == 0) { await transaction.RollbackAsync(); return false; }

        var request = step.ApprovalRequest;
        ApprovalStep? nextStep = null;

        if (!dto.IsApproved)
        {
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
            nextStep = await db.ApprovalSteps
                .Where(s => s.ApprovalRequestId == dto.ApprovalRequestId
                         && s.StepOrder > step.StepOrder
                         && s.ApprovalStatus == ApprovalStatus.Pending)
                .OrderBy(s => s.StepOrder)
                .FirstOrDefaultAsync();
        }

        var newRequestStatus = !dto.IsApproved ? ApprovalStatus.Rejected
            : nextStep is null ? ApprovalStatus.Approved : ApprovalStatus.InProgress;

        // 整单状态也做成"抢占式"更新：只有整单目前还没被撤销（仍是待审批/审批中）才允许改——
        // 跟申请人几乎同一时刻点的"撤销"撞车时，谁先提交生效，另一边这里会发现整单已经不是
        // 自己以为的状态，抢占失败，整个事务连同上面刚抢到的节点状态一起回滚，不会出现
        // "显示已撤销、但考勤已经按通过回写"这种结果不一致的情况。
        var requestClaimed = await db.ApprovalRequests
            .Where(a => a.Id == request.Id
                     && (a.ApprovalStatus == ApprovalStatus.Pending || a.ApprovalStatus == ApprovalStatus.InProgress))
            .ExecuteUpdateAsync(a => a
                .SetProperty(x => x.ApprovalStatus, newRequestStatus)
                .SetProperty(x => x.UpdatedAt, DateTime.Now));
        if (requestClaimed == 0) { await transaction.RollbackAsync(); return false; }
        request.ApprovalStatus = newRequestStatus;   // 同步内存对象，后面回写考勤/通知要用

        await db.SaveChangesAsync();   // 落盘"驳回时后续节点作废"这几条改动

        if (dto.IsApproved && nextStep is null)
            await attendanceService.UpdateAttendanceAfterApprovalAsync(request.Id);   // 整单通过，回写考勤

        await transaction.CommitAsync();

        if (dto.IsApproved && nextStep is not null)
            await NotifyNextApproverAsync(request, nextStep);   // 还有下一级，通知下一个审批人
        await NotifyApplicantAsync(request);   // 通知申请人结果
        return true;
    }

    /// <summary>申请人撤销自己的申请（只有“待审批”状态能撤）。</summary>
    public async Task<bool> CancelApprovalAsync(int userId, int approvalRequestId)
    {
        // 跟 HandleApprovalAsync 用同一套"抢占式"条件更新：只有整单现在确实还是 Pending 才能撤，
        // 直接在数据库层面做条件更新，不会有"先查到还是 Pending、还没来得及写就被审批人抢先处理掉"
        // 这种先查后写之间的竞态窗口。
        var claimed = await db.ApprovalRequests
            .Where(a => a.Id == approvalRequestId && a.ApplicantUserId == userId && a.ApprovalStatus == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(a => a
                .SetProperty(x => x.ApprovalStatus, ApprovalStatus.Cancelled)
                .SetProperty(x => x.UpdatedAt, DateTime.Now));
        if (claimed == 0) return false;

        // 还没处理的审批节点要一并作废，不然撤销形同虚设：审批人那边这个节点还显示"待审批"，
        // 真去点了"通过"的话，HandleApprovalAsync 会发现整单已经不是 Pending/InProgress、
        // 抢占失败并回滚，不会出现一张已经撤销的申请被"复活"生效的情况。
        await db.ApprovalSteps
            .Where(s => s.ApprovalRequestId == approvalRequestId && s.ApprovalStatus == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ApprovalStatus, ApprovalStatus.Cancelled));

        return true;
    }

    /// <summary>分页查询审批记录（多条件过滤）。</summary>
    public async Task<(List<ApprovalRequestDto> Items, int Total)> QueryApprovalsAsync(ApprovalQueryDto q, HashSet<int>? deptIds = null)
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
        if (deptIds is not null)        query = query.Where(a => a.Applicant.DepartmentId != null && deptIds.Contains(a.Applicant.DepartmentId.Value));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.SubmittedAt)
            .Skip((q.PageIndex - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync();

        return (items.Select(ToDto).ToList(), total);
    }

    /// <summary>查申请详情（带权限校验：只有申请人本人/该单审批人/管理员文员能看）。</summary>
    public async Task<ApprovalRequestDto?> GetApprovalDetailAsync(int id, int requesterUserId, bool isManager, HashSet<int>? managerVisibleDeptIds = null)
    {
        var request = await db.ApprovalRequests
            .Include(a => a.Applicant).ThenInclude(u => u.Department)
            .Include(a => a.ApprovalSteps).ThenInclude(s => s.Approver)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (request is null) return null;

        // "管理员/文员"这条路径，分公司管理员还要求申请人部门落在自己范围内——光有 ManagePolicy
        // 身份不代表能看到别的分公司的申请详情，只是不受限的总部管理员才能看全部
        bool managerAllowed = isManager && (managerVisibleDeptIds is null
            || (request.Applicant.DepartmentId.HasValue && managerVisibleDeptIds.Contains(request.Applicant.DepartmentId.Value)));

        // 允许查看的三种人：管理员/文员（范围内）、申请人本人、这张单的某个审批人——
        // "审批人"这一档额外复核一遍现在的范围（申请人可能调岗后已经不归这个审批人管了）
        bool allowed = managerAllowed
                       || request.ApplicantUserId == requesterUserId
                       || (request.ApprovalSteps.Any(s => s.ApproverUserId == requesterUserId)
                           && await ApproverCoversApplicantAsync(requesterUserId, request.Applicant.DepartmentId));
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

        // 逐层递进：当前应处理的是”最小 StepOrder 的待审批节点”，只有它的审批人才算轮到；
        // 再复核一遍我现在管不管得到申请人现在所在的部门——申请人调岗后原来的审批节点不会自动失效，
        // 不加这一步的话，调走前留下的旧申请会一直挂在原公司审批人的待办里，能看到姓名/请假理由等隐私
        var result = new List<ApprovalRequestDto>();
        foreach (var a in candidates)
        {
            var activeOrder = a.ApprovalSteps
                .Where(s => s.ApprovalStatus == ApprovalStatus.Pending)
                .Min(s => s.StepOrder);
            var isMyTurn = a.ApprovalSteps.Any(s => s.StepOrder == activeOrder
                                         && s.ApproverUserId == approverUserId
                                         && s.ApprovalStatus == ApprovalStatus.Pending);
            if (!isMyTurn) continue;
            if (!await ApproverCoversApplicantAsync(approverUserId, a.Applicant.DepartmentId)) continue;
            result.Add(ToDto(a));
        }
        return result;
    }

    /// <summary>查”我已经审批过”的记录：只要我在这张单里有一个已通过/已驳回的节点就算数，
    /// 不管这张单最终整体是不是还在走后续流程；按我自己处理的时间倒序排列。</summary>
    public async Task<List<ApprovalRequestDto>> GetHandledByApproverAsync(int approverUserId)
    {
        var items = await db.ApprovalRequests
            .Include(a => a.Applicant).ThenInclude(u => u.Department)
            .Include(a => a.ApprovalSteps).ThenInclude(s => s.Approver)
            .Where(a => a.ApprovalSteps.Any(s => s.ApproverUserId == approverUserId
                                               && s.ApprovalStatus != ApprovalStatus.Pending))
            .ToListAsync();

        return items
            .Select(a => new
            {
                Request     = a,
                MyHandledAt = a.ApprovalSteps
                    .Where(s => s.ApproverUserId == approverUserId && s.ApprovalStatus != ApprovalStatus.Pending)
                    .Max(s => s.HandledAt)
            })
            .OrderByDescending(x => x.MyHandledAt)
            .Select(x => ToDto(x.Request))
            .ToList();
    }

    /// <summary>查”我提交的”申请列表（可按状态过滤）。</summary>
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
        // 按类型分开计数（原来不管什么类型混在一起数），不然同一天 BK/QJ/JB/CC 交替提交时，
        // 单号里的流水号会一格一格互相"抢位"，看起来像中间缺了号，其实只是没按前缀分开数
        var count = await db.ApprovalRequests.CountAsync(a => a.SubmittedAt.Date == DateTime.Today && a.ApprovalType == type) + 1;
        return $"{prefix}{date}{count:D4}";   // 如 QJ202606250001
    }

    // ── 私有方法 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 为申请创建审批节点：
    /// 第一级——考勤组配了审批人名单 → 必须是员工自己从名单里选的那个人（名单只能是班组长）；
    /// 组里没配名单 → 回退"直属上级"；连上级也没有 → 兜底指派一名管理员/文员，避免申请永远没人审。
    /// 第二级——仅当考勤组的审批层级配置为"二级审批"时才会生成：自动追加申请人自己的"直属上级"作为第二个节点，
    /// 直属上级没配时同样兜底指派一名管理员/文员。没能生成第一级节点时不会生成第二级，避免破坏"先一级后二级"的顺序。
    /// </summary>
    private async Task CreateApprovalStepsAsync(ApprovalRequest request, User applicant, int? selectedApproverUserId)
    {
        var group = applicant.AttendanceGroupId.HasValue
            ? await db.AttendanceGroups.FindAsync(applicant.AttendanceGroupId.Value)
            : null;

        var groupApproverIds = await db.AttendanceGroupApprovers
            .Where(a => a.AttendanceGroupId == applicant.AttendanceGroupId)
            .Select(a => a.UserId)
            .ToListAsync();

        int? approverId;
        if (groupApproverIds.Count > 0)
        {
            // 组里配了审批人名单：员工必须选中名单里的一个人，不能自己瞎填/绕过名单，也不能选自己——
            // 班组长/主管本人如果恰好也在自己所在考勤组的审批人名单里，不能自己批自己提交的申请。
            if (selectedApproverUserId is null || selectedApproverUserId == applicant.Id
                || !groupApproverIds.Contains(selectedApproverUserId.Value))
                throw new InvalidOperationException("请选择有效的审批人");
            approverId = selectedApproverUserId;
        }
        else
        {
            // 组里没配名单：退回直属上级；没上级就兜底找个管理员/文员
            approverId = applicant.SupervisorUserId ?? await ResolveFallbackApproverAsync(applicant);
        }

        if (approverId.HasValue)
        {
            db.ApprovalSteps.Add(new ApprovalStep
            {
                ApprovalRequestId = request.Id,
                ApproverUserId    = approverId.Value,
                StepOrder         = 1,
                ApprovalStatus    = ApprovalStatus.Pending,
                CreatedAt         = DateTime.Now
            });

            // 二级审批：一级节点通过后再自动追加一个申请人"直属上级"的节点
            if (group?.ApprovalLevel == ApprovalLevelType.Level2)
            {
                var level2ApproverId = applicant.SupervisorUserId ?? await ResolveFallbackApproverAsync(applicant);
                if (level2ApproverId.HasValue)
                    db.ApprovalSteps.Add(new ApprovalStep
                    {
                        ApprovalRequestId = request.Id,
                        ApproverUserId    = level2ApproverId.Value,
                        StepOrder         = 2,
                        ApprovalStatus    = ApprovalStatus.Pending,
                        CreatedAt         = DateTime.Now
                    });
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 兜底审批人：没审批流也没上级时，指派一名在职管理员/文员来审。
    /// 优先同考勤组的，其次随便一个，并排除申请人自己，避免”无人可审”。
    /// 候选人必须”管得到”申请人所在部门——总部超级管理员（ScopedDepartmentId 为空）恒可以；
    /// 分公司管理员/文员只有申请人部门落在自己范围内才算数。不加这道限制的话，没配审批人名单、
    /// 又没设直属上级的员工，申请单可能被指派给完全不相干的别的分公司管理员去审，对方还能看到
    /// 申请人的请假/出差理由等隐私信息，通过后还会回写到本不该他管的考勤记录上。
    /// 申请人自己没有部门（DepartmentId 为空）时，只有总部超级管理员能兜底——跟”无部门归属的数据
    /// 只总部可见”是同一个口径。
    /// </summary>
    private async Task<int?> ResolveFallbackApproverAsync(User applicant)
    {
        var ancestorIds = await GetAncestorDeptIdsAsync(applicant.DepartmentId);
        var managers = await db.Users
            .Where(u => u.IsActive
                     && (u.Role == UserRole.Admin || u.Role == UserRole.Clerk)
                     && u.Id != applicant.Id
                     && (u.ScopedDepartmentId == null
                         || (applicant.DepartmentId != null && ancestorIds.Contains(u.ScopedDepartmentId.Value))))
            .Select(u => new { u.Id, u.AttendanceGroupId })
            .ToListAsync();
        if (managers.Count == 0) return null;
        var sameGroup = managers.FirstOrDefault(m => m.AttendanceGroupId == applicant.AttendanceGroupId);
        return (sameGroup ?? managers[0]).Id;   // 优先同组，否则取第一个
    }

    /// <summary>取某部门自己 + 一路向上所有祖先部门的 id 集合（deptId 为空时返回空集合）——
    /// 用来判断”某个 ScopedDepartmentId 是否覆盖这个部门”：只要 ScopedDepartmentId 出现在这个集合里，
    /// 说明这个部门是那个范围根节点的自己或下级，落在对方的管理范围内。</summary>
    private async Task<HashSet<int>> GetAncestorDeptIdsAsync(int? deptId)
    {
        var ids = new HashSet<int>();
        var cur = deptId;
        while (cur.HasValue && ids.Add(cur.Value))
            cur = await db.Departments.Where(d => d.Id == cur.Value).Select(d => d.ParentId).FirstOrDefaultAsync();
        return ids;
    }

    /// <summary>这个审批人现在还管不管得到申请人现在所在的部门——审批节点生成后 ApproverUserId 是固定的，
    /// 不会随申请人后续调岗自动失效；这里在”查待办/查详情/处理审批”这几个入口现查一遍当前范围，
    /// 而不是只信节点上那个写死的审批人 id，避免申请人调到别的分公司后，原公司的审批人还能继续
    /// 看到/处理这张单（PII 泄露 + 通过后回写到已经不归自己管的考勤记录）。</summary>
    private async Task<bool> ApproverCoversApplicantAsync(int approverUserId, int? applicantDeptId)
    {
        var scopeId = await db.Users.Where(u => u.Id == approverUserId).Select(u => u.ScopedDepartmentId).FirstOrDefaultAsync();
        if (scopeId is null) return true;          // 总部超级管理员，不受限
        if (applicantDeptId is null) return false; // 申请人没有部门归属，只总部可见
        var ancestorIds = await GetAncestorDeptIdsAsync(applicantDeptId);
        return ancestorIds.Contains(scopeId.Value);
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
                StepOrder      = s.StepOrder,
                ApproverUserId = s.ApproverUserId,
                ApproverName   = s.Approver.RealName,
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
