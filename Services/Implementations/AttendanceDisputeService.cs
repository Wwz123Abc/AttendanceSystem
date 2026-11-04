using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 员工考勤核对 / 异议（2026-11-03 新增，规则见 <see cref="IAttendanceDisputeService"/>）：
/// ① 只能核对/提交"近 7 个考勤日"（今天往前 6 天 ~ 今天），服务端强制，前端改日期也绕不过去；
/// ② 同一考勤日只能有一张"待处理/处理中"的异议；被驳回后允许再提一次（同一考勤日最多 2 次）；
/// ③ 每个员工每天最多提 3 张（防刷）；
/// ④ 异议**不改**考勤数据：管理员仍用"手动补卡/手动调整"改数据，改完由 <see cref="AutoResolveAsync"/> 或页面上手动关单；
/// ⑤ 提交时把记录快照存下来，事后记录被改也看得到当时是什么样。
/// </summary>
public class AttendanceDisputeService(AttendanceDbContext db, IDeptScopeService deptScope, TimeProvider? timeProvider = null) : IAttendanceDisputeService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>员工能核对的考勤日数（含今天）。</summary>
    public const int VerifyDaysCount = 7;
    public int VerifyDays => VerifyDaysCount;
    /// <summary>同一考勤日最多提几次（一次正常 + 被驳回后允许再提一次）。</summary>
    public const int MaxPerWorkDate = 2;
    /// <summary>每人每天最多提几张（防刷）。</summary>
    public const int MaxPerSubmitDay = 3;

    private static DateOnly Today(TimeProvider c) => DateOnly.FromDateTime(c.LocalNow());

    private static string DayText(DateOnly d) => WeekdayText.Zh(d);

    /// <summary>状态徽标样式（Bootstrap）。文字统一用 <see cref="AttendanceService.StatusText"/>，避免和报表/页面说法不一致。</summary>
    private static string BadgeCss(AttendanceStatus s) => s switch
    {
        AttendanceStatus.Normal       => "bg-success",
        AttendanceStatus.Late         => "bg-warning text-dark",
        AttendanceStatus.EarlyLeave   => "bg-warning text-dark",
        AttendanceStatus.Absent       => "bg-danger",
        AttendanceStatus.Holiday      => "bg-info text-dark",
        AttendanceStatus.OnLeave      => "bg-info text-dark",
        AttendanceStatus.Overtime     => "bg-primary",
        AttendanceStatus.NotPunched   => "bg-secondary",
        AttendanceStatus.BusinessTrip => "bg-info text-dark",
        _                             => "bg-light text-dark"
    };

    private static string TimeText(DateTime? t, DateOnly workDate)
        => t is not { } v ? "--" : DateOnly.FromDateTime(v) == workDate ? v.ToString("HH:mm") : v.ToString("MM-dd HH:mm");

    public async Task<List<AttendanceVerifyRowDto>> GetVerifyListAsync(int userId, CancellationToken ct = default)
    {
        var today = Today(clock);
        var from  = today.AddDays(-(VerifyDaysCount - 1));

        var records = (await db.AttendanceRecords.AsNoTracking()
                .Where(r => r.UserId == userId && r.WorkDate >= from && r.WorkDate <= today).ToListAsync(ct))
            .ToDictionary(r => r.WorkDate);
        var shifts = (await db.ShiftAssignments.AsNoTracking().Include(a => a.ShiftSchedule)
                .Where(a => a.UserId == userId && a.WorkDate >= from && a.WorkDate <= today).ToListAsync(ct))
            .ToDictionary(a => a.WorkDate, a => a.ShiftSchedule);

        var disputeMap = (await db.AttendanceDisputes.AsNoTracking()
                .Where(d => d.UserId == userId && d.WorkDate >= from && d.WorkDate <= today)
                .OrderBy(d => d.Id).ToListAsync(ct))
            .GroupBy(d => d.WorkDate)
            .ToDictionary(g => g.Key, g => g.Last());

        // 这一周还在审批的加班单：员工常问"我加了班怎么没算加班"，页面上提前说明"审批通过后才计入"
        var weekStart = from.ToDateTime(TimeOnly.MinValue);
        var weekEnd   = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var pendingOt = await db.ApprovalRequests.AsNoTracking()
            .Where(a => a.ApplicantUserId == userId && a.ApprovalType == ApprovalType.Overtime
                        && (a.ApprovalStatus == ApprovalStatus.Pending || a.ApprovalStatus == ApprovalStatus.InProgress)
                        && a.OvertimeStartTime != null && a.OvertimeEndTime != null
                        && a.OvertimeStartTime < weekEnd && a.OvertimeEndTime > weekStart)
            .Select(a => new { a.RequestNo, a.OvertimeStartTime, a.OvertimeEndTime, a.OvertimeDurationHours, a.ApprovalStatus })
            .ToListAsync(ct);

        var rows = new List<AttendanceVerifyRowDto>();
        for (var d = from; d <= today; d = d.AddDays(1))
        {
            records.TryGetValue(d, out var rec);
            shifts.TryGetValue(d, out var shift);
            if (rec is null && shift is null) continue;   // 既没记录也没排班：没什么可核对的

            var row = new AttendanceVerifyRowDto
            {
                WorkDate      = d,
                DayOfWeekText = DayText(d),
                HasRecord     = rec is not null,
                IsScheduled   = shift is not null
            };
            if (shift is not null)
            {
                row.ShiftName     = shift.ShiftName;
                row.ShiftTimeText = $"{shift.WorkStartTime:HH\\:mm}–{(shift.IsCrossDay ? "次日 " : "")}{shift.WorkEndTime:HH\\:mm}";
            }
            if (rec is not null)
            {
                row.ClockInText      = TimeText(rec.ClockInTime, d);
                row.ClockOutText     = TimeText(rec.ClockOutTime, d);
                row.WorkHours        = rec.ActualWorkHours;
                row.OvertimeHours    = rec.OvertimeHours;
                row.LateMinutes      = rec.LateMinutes;
                row.EarlyLeaveMinutes = rec.EarlyLeaveMinutes;
                row.StatusText       = AttendanceService.StatusText(rec.AttendanceStatus);
                row.StatusCssClass   = BadgeCss(rec.AttendanceStatus);
            }
            else
            {
                (row.StatusText, row.StatusCssClass) = ("没有记录", "bg-secondary");
            }

            // 那天在审批中的加班单
            var dayFrom = d.ToDateTime(TimeOnly.MinValue);
            var dayTo   = d.AddDays(1).ToDateTime(TimeOnly.MinValue);
            foreach (var ot in pendingOt.Where(x => x.OvertimeStartTime!.Value < dayTo && x.OvertimeEndTime!.Value > dayFrom))
            {
                row.PendingOvertimeHours += ot.OvertimeDurationHours ?? 0m;
                row.PendingOvertimeTexts.Add($"{ot.OvertimeDurationHours ?? 0m:0.##} 小时（加班单 {ot.RequestNo} 待审批）");
            }

            if (disputeMap.TryGetValue(d, out var dp))
            {
                row.DisputeId       = dp.Id;
                row.DisputeStatus   = dp.Status;
                row.DisputeTypeText = dp.Type.Text();
                row.DisputeReply    = dp.Reply;
            }
            rows.Add(row);
        }
        return rows;
    }

    public async Task<int> SubmitAsync(int userId, DateOnly workDate, AttendanceDisputeType type, string reason, string? expectedText, CancellationToken ct = default)
    {
        var now   = clock.LocalNow();
        var today = DateOnly.FromDateTime(now);
        var from  = today.AddDays(-(VerifyDaysCount - 1));

        if (workDate > today)
            throw new BusinessException("不能对还没到的日期提异议");
        if (workDate < from)
            throw new BusinessException($"只能核对近 {VerifyDaysCount} 天的考勤（{from:MM-dd} 之后）；更早的记录请直接联系文员");

        reason = (reason ?? "").Trim();
        if (reason.Length < 5)
            throw new BusinessException("请把情况说明清楚（至少 5 个字），方便管理员核对");
        if (reason.Length > 500) reason = reason[..500];
        expectedText = string.IsNullOrWhiteSpace(expectedText) ? null : expectedText.Trim();
        if (expectedText is { Length: > 200 }) expectedText = expectedText[..200];

        var employee = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId }).FirstOrDefaultAsync(ct)
            ?? throw new BusinessException("账号不存在");

        var existing = await db.AttendanceDisputes.Where(d => d.UserId == userId && d.WorkDate == workDate).ToListAsync(ct);
        if (existing.Any(d => d.Status is AttendanceDisputeStatus.Pending or AttendanceDisputeStatus.Processing))
            throw new BusinessException("这一天的异议已经在处理中，请等待处理结果，不用重复提交");
        if (existing.Count >= MaxPerWorkDate)
            throw new BusinessException("这一天已经提过异议并被处理过了；如果还有问题，请直接联系文员");

        var submittedToday = await db.AttendanceDisputes.CountAsync(d => d.UserId == userId && d.CreatedAt >= now.Date, ct);
        if (submittedToday >= MaxPerSubmitDay)
            throw new BusinessException($"今天已经提了 {MaxPerSubmitDay} 张异议，请先等处理结果；紧急情况请直接联系文员");

        var rec = await db.AttendanceRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == workDate, ct);
        var hasShift = await db.ShiftAssignments.AnyAsync(a => a.UserId == userId && a.WorkDate == workDate, ct);
        if (rec is null && !hasShift)
            throw new BusinessException("这一天既没有考勤记录也没有排班；如果确实上了班，请直接联系文员补记录");

        var snapshot = rec is null
            ? "（提交时这一天没有考勤记录）"
            : $"上班 {TimeText(rec.ClockInTime, workDate)}、下班 {TimeText(rec.ClockOutTime, workDate)}；"
              + $"正班 {rec.ActualWorkHours:0.##}h、加班 {rec.OvertimeHours:0.##}h；状态 {AttendanceService.StatusText(rec.AttendanceStatus)}"
              + (string.IsNullOrWhiteSpace(rec.ApprovalNote) ? "" : $"；说明 {rec.ApprovalNote}");

        var dispute = new AttendanceDispute
        {
            UserId      = userId,
            WorkDate    = workDate,
            RecordId    = rec?.Id,
            Snapshot    = snapshot.Length > 500 ? snapshot[..500] : snapshot,
            Type        = type,
            Reason      = reason,
            ExpectedText = expectedText,
            Status      = AttendanceDisputeStatus.Pending,
            CreatedAt   = now,
            UpdatedAt   = now
        };
        db.AttendanceDisputes.Add(dispute);
        await db.SaveChangesAsync(ct);

        // 通知：管理员 + 部门范围覆盖该员工的文员（按部门范围过滤，2026-11-03 用户强调）
        var handlerIds = await GetHandlerUserIdsAsync(employee.DepartmentId, ct);
        if (handlerIds.Count > 0)
        {
            var content = $"{employee.RealName}（{employee.EmployeeNo}）对 {workDate:MM-dd}（{DayText(workDate)}）的考勤提出异议："
                          + $"{type.Text()}。说明：{reason}";
            db.Notifications.AddRange(handlerIds.Select(id => new Notification
            {
                UserId = id, Title = "考勤异议待处理", Content = content,
                NotificationType = NotificationTypes.AttendanceDispute, RelatedId = dispute.Id, CreatedAt = now
            }));
            await db.SaveChangesAsync(ct);
        }
        return dispute.Id;
    }

    public async Task CancelAsync(int userId, int disputeId, CancellationToken ct = default)
    {
        var d = await db.AttendanceDisputes.FirstOrDefaultAsync(x => x.Id == disputeId && x.UserId == userId, ct)
            ?? throw new BusinessException("异议单不存在");
        if (d.Status != AttendanceDisputeStatus.Pending)
            throw new BusinessException("只有\"待处理\"的异议才能撤回（已经有人接手了）");
        d.Status    = AttendanceDisputeStatus.Cancelled;
        d.UpdatedAt = clock.LocalNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<AttendanceDisputeListResultDto> ListForHandlerAsync(AttendanceDisputeQueryDto query, HashSet<int>? allowedDeptIds, CancellationToken ct = default)
    {
        var q = BaseHandlerQuery(allowedDeptIds);
        if (query.Status is { } st) q = q.Where(x => x.Dispute.Status == st);

        var pendingTotal = await BaseHandlerQuery(allowedDeptIds)
            .CountAsync(x => x.Dispute.Status == AttendanceDisputeStatus.Pending || x.Dispute.Status == AttendanceDisputeStatus.Processing, ct);
        var total  = await q.CountAsync(ct);
        var size   = query.PageSize <= 0 ? 50 : Math.Min(query.PageSize, 200);
        var page   = query.Page <= 0 ? 1 : query.Page;
        var items = await q
            .OrderBy(x => x.Dispute.Status == AttendanceDisputeStatus.Pending ? 0 : 1)
            .ThenByDescending(x => x.Dispute.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .Select(x => new AttendanceDisputeListItemDto
            {
                Id = x.Dispute.Id,
                UserId = x.Dispute.UserId,
                EmployeeNo = x.EmployeeNo,
                RealName = x.RealName,
                DeptName = x.DeptName,
                WorkDate = x.Dispute.WorkDate,
                Type = x.Dispute.Type,
                Reason = x.Dispute.Reason,
                ExpectedText = x.Dispute.ExpectedText,
                Snapshot = x.Dispute.Snapshot,
                Status = x.Dispute.Status,
                CreatedAt = x.Dispute.CreatedAt,
                HandlerName = x.Dispute.HandlerName,
                HandledAt = x.Dispute.HandledAt,
                Reply = x.Dispute.Reply
            })
            .ToListAsync(ct);
        foreach (var i in items) { i.DayOfWeekText = DayText(i.WorkDate); i.TypeText = i.Type.Text(); }
        return new AttendanceDisputeListResultDto { Items = items, Total = total, PendingTotal = pendingTotal };
    }

    private IQueryable<HandlerRow> BaseHandlerQuery(HashSet<int>? allowedDeptIds)
    {
        var q = from d in db.AttendanceDisputes.AsNoTracking()
                join u in db.Users on d.UserId equals u.Id
                join dept in db.Departments on u.DepartmentId equals (int?)dept.Id into depts
                from dept in depts.DefaultIfEmpty()
                select new HandlerRow
                {
                    Dispute = d,
                    EmployeeNo = u.EmployeeNo,
                    RealName = u.RealName,
                    DepartmentId = u.DepartmentId,
                    DeptName = dept == null ? null : dept.DeptName
                };
        if (allowedDeptIds is not null)
            q = q.Where(x => x.DepartmentId != null && allowedDeptIds.Contains(x.DepartmentId.Value));
        return q;
    }

    private sealed class HandlerRow
    {
        public AttendanceDispute Dispute { get; set; } = null!;
        public string EmployeeNo { get; set; } = "";
        public string RealName { get; set; } = "";
        public int? DepartmentId { get; set; }
        public string? DeptName { get; set; }
    }

    public async Task<int> CountPendingAsync(HashSet<int>? allowedDeptIds, CancellationToken ct = default)
        => await BaseHandlerQuery(allowedDeptIds)
            .CountAsync(x => x.Dispute.Status == AttendanceDisputeStatus.Pending || x.Dispute.Status == AttendanceDisputeStatus.Processing, ct);

    public async Task HandleAsync(int disputeId, int handlerUserId, string handlerName, bool accept, string reply, HashSet<int>? allowedDeptIds = null, CancellationToken ct = default)
    {
        var d = await db.AttendanceDisputes.FirstOrDefaultAsync(x => x.Id == disputeId, ct)
            ?? throw new BusinessException("异议单不存在");
        if (d.Status is AttendanceDisputeStatus.Resolved or AttendanceDisputeStatus.Rejected or AttendanceDisputeStatus.Cancelled)
            throw new BusinessException("这张异议单已经处理过了");

        // 分公司管理员/文员只能处理自己范围内的员工（2026-11-03 用户强调：注意各分公司权限）
        if (allowedDeptIds is not null)
        {
            var deptId = await db.Users.Where(u => u.Id == d.UserId).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);
            if (deptId is not { } dep || !allowedDeptIds.Contains(dep))
                throw new BusinessException("无权处理该员工的异议（不在你的管理范围内）");
        }

        reply = (reply ?? "").Trim();
        if (reply.Length < 2) throw new BusinessException("请填写给员工的处理结果/回复（员工会看到）");
        if (reply.Length > 500) reply = reply[..500];

        var now = clock.LocalNow();
        d.Status        = accept ? AttendanceDisputeStatus.Resolved : AttendanceDisputeStatus.Rejected;
        d.HandlerUserId = handlerUserId;
        d.HandlerName   = handlerName;
        d.HandledAt     = now;
        d.Reply         = reply;
        d.UpdatedAt     = now;
        db.Notifications.Add(new Notification
        {
            UserId = d.UserId,
            Title = "考勤异议已处理",
            Content = $"{d.WorkDate:MM-dd}（{DayText(d.WorkDate)}）的异议已{(accept ? "处理" : "驳回")}：{reply}",
            NotificationType = NotificationTypes.AttendanceDispute,
            RelatedId = d.Id,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> AutoResolveAsync(int userId, DateOnly workDate, string note, CancellationToken ct = default)
    {
        var list = await db.AttendanceDisputes
            .Where(d => d.UserId == userId && d.WorkDate == workDate
                        && (d.Status == AttendanceDisputeStatus.Pending || d.Status == AttendanceDisputeStatus.Processing))
            .ToListAsync(ct);
        if (list.Count == 0) return 0;

        var now = clock.LocalNow();
        note = string.IsNullOrWhiteSpace(note) ? "考勤已按你的异议调整" : note.Trim();
        foreach (var d in list)
        {
            d.Status      = AttendanceDisputeStatus.Resolved;
            d.HandledAt ??= now;
            d.HandlerName ??= "系统";
            d.Reply       = note;
            d.UpdatedAt   = now;
        }
        db.Notifications.AddRange(list.Select(d => new Notification
        {
            UserId = d.UserId,
            Title = "考勤异议已处理",
            Content = $"{d.WorkDate:MM-dd}（{DayText(d.WorkDate)}）的考勤已调整：{note}",
            NotificationType = NotificationTypes.AttendanceDispute,
            RelatedId = d.Id,
            CreatedAt = now
        }));
        await db.SaveChangesAsync(ct);
        return list.Count;
    }

    /// <summary>
    /// 谁该收到异议：所有管理员 + 部门范围覆盖该员工部门的文员（不受范围限制的文员也收到）。
    /// 各分公司管理员（Clerk 带 ScopedDepartmentId）只会收到自己范围内的，避免跨公司收到别人的异议。
    /// </summary>
    private async Task<List<int>> GetHandlerUserIdsAsync(int? employeeDeptId, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.Role == UserRole.Admin || u.Role == UserRole.Clerk))
            .Select(u => new { u.Id, u.Role, u.ScopedDepartmentId })
            .ToListAsync(ct);

        var result = new List<int>();
        foreach (var u in users)
        {
            if (u.Role == UserRole.Admin) { result.Add(u.Id); continue; }
            if (u.ScopedDepartmentId is null) { result.Add(u.Id); continue; }   // 不受部门范围限制的文员
            if (employeeDeptId is not { } deptId) continue;                      // 没部门的员工：只有管理员处理
            var scope = await deptScope.GetSubtreeIdsAsync(u.ScopedDepartmentId.Value);
            if (scope.Contains(deptId)) result.Add(u.Id);
        }
        return result.Distinct().ToList();
    }
}
