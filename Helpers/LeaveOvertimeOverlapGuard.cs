using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;

namespace AttendanceSystem.Helpers;

/// <summary>
/// 加班与请假时段重叠校验（2026-11-05 用户要求）：
/// 同一段时间既批了假、又报了加班，会按"请假封顶 + 加班时长"重复算钱。
/// 提交申请时查"另一类已批准的单子"，审批通过回写时再查一次（防止提交时对方还没批）。
///
/// ★ 判断"重叠"不能拿假单的整段区间直接比：一张多天假单（比如 10-21~11-22 的事假）的区间
///   连晚上、周末都覆盖了，但那段时间**并没有计入请假**——请假只按"每天和班次上班时段的交集"
///   计入（事假/病假/年假/调休不占休息日；婚假/产假/丧假按自然日占用整天）。
///   所以这里逐天还原"那天实际计入的请假时段"，再和加班比，避免把长假期间的周末加班、晚上加班误拦。
/// 出差不参与这条校验（出差期间确实可能有加班）。
///
/// ★ 并发保护（2026-11-06）：审批回写前必须拿到"申请人那一行"的数据库行锁；**拿不到锁就失败回滚**，
///   绝不能在没加锁的情况下继续审批——那样两张冲突单仍可能同时通过、重复计薪。
///   提交申请路径不锁（它没有开启事务，FOR UPDATE 会立即释放，锁了也没用），重叠检查仍然照做。
/// </summary>
public static class LeaveOvertimeOverlapGuard
{
    /// <summary>有重叠就抛 <see cref="BusinessException"/>（消息给员工/审批人看）；没有重叠正常返回。</summary>
    /// <param name="submittingLeaveType">本次提交的是请假时，传上假别（决定按自然日还是按班次时段计请假）；提交加班时传 null。</param>
    /// <param name="requireApplicantLock">是否必须拿到申请人行锁（只有"审批通过回写、处在事务里"的调用方传 true）。
    /// 拿不到锁会记错误日志并抛业务异常，让外层事务整单回滚，单子保持待审批。</param>
    /// <param name="logger">用来记录"拿不到锁"这类错误（提交路径可以不传）。</param>
    public static async Task EnsureNoOverlapAsync(
        AttendanceDbContext db, int applicantUserId, DateTime start, DateTime end,
        ApprovalType submittingType, int? excludeRequestId = null,
        LeaveType? submittingLeaveType = null, bool requireApplicantLock = false,
        ILogger? logger = null, CancellationToken ct = default)
    {
        if (submittingType is not (ApprovalType.Overtime or ApprovalType.Leave)) return;
        var opposite = submittingType == ApprovalType.Overtime ? ApprovalType.Leave : ApprovalType.Overtime;

        // 并发审批的竞态：两张冲突单分别在独立事务里审批时，双方可能都读到"对方还没批准"，然后各写各的考勤
        // （2026-11-06 审查发现 P2）。审批回写本身在 ApprovalService.HandleApprovalAsync 的事务里，
        // 先锁住"申请人"这一行，同一个人的请假/加班就会串行处理，后一个一定能看到前一个已批准的结果。
        // ★ 锁失败必须让本次审批失败：如果拿不到锁还继续，并发窗口就重新出现了（2026-11-06 用户要求）。
        // 只有 MySQL/MariaDB 支持 FOR UPDATE（单元测试用的 SQLite 没有这句，直接跳过）。
        if (requireApplicantLock
            && db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT Id FROM `User` WHERE Id = {0} FOR UPDATE",
                    new object[] { applicantUserId }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex,
                    "审批回写前无法锁定申请人 {UserId} 的行（请假/加班重叠校验），本次审批已回滚", applicantUserId);
                throw new BusinessException(
                    "系统繁忙：无法锁定该员工的审批记录（可能有并发审批正在进行），本次审批未生效，请稍后重试", ex);
            }
        }

        var others = await db.ApprovalRequests.AsNoTracking()
            .Where(a => a.ApplicantUserId == applicantUserId
                        && a.ApprovalType == opposite
                        && a.ApprovalStatus == ApprovalStatus.Approved
                        && (excludeRequestId == null || a.Id != excludeRequestId))
            .Select(a => new { a.RequestNo, a.OvertimeStartTime, a.OvertimeEndTime, a.LeaveStartTime, a.LeaveEndTime, a.LeaveType })
            .ToListAsync(ct);

        foreach (var o in others)
        {
            DateTime leaveStart, leaveEnd, otStart, otEnd;
            LeaveType? leaveType;
            if (submittingType == ApprovalType.Overtime)   // 本次是加班，对方是"已批准的请假"
            {
                if (o.LeaveStartTime is not { } ls || o.LeaveEndTime is not { } le) continue;
                leaveStart = ls; leaveEnd = le; leaveType = o.LeaveType;
                otStart = start; otEnd = end;
            }
            else                                            // 本次是请假，对方是"已批准的加班"
            {
                if (o.OvertimeStartTime is not { } os || o.OvertimeEndTime is not { } oe) continue;
                leaveStart = start; leaveEnd = end; leaveType = submittingLeaveType ?? o.LeaveType;
                otStart = os; otEnd = oe;
            }
            if (otStart >= otEnd) continue;
            if (!await CountedLeaveOverlapsAsync(db, applicantUserId, leaveStart, leaveEnd, leaveType, otStart, otEnd, ct))
                continue;

            throw new BusinessException(submittingType == ApprovalType.Overtime
                ? $"这段时间已经批准了请假（{o.RequestNo}），和这次加班的时间重叠，会按请假+加班重复算钱；"
                  + "请把加班时间避开请假时段，或先联系文员核对该请假单"
                : $"这段时间已经批准了加班（{o.RequestNo}），和这次请假的时间重叠，会按请假+加班重复算钱；"
                  + "请先调整或撤销那张加班单，或把请假时间避开加班时段");
        }
    }

    /// <summary>
    /// 逐天还原"这天实际计入请假的时段"，看和 [otStart, otEnd) 有没有交集。
    /// 事假/病假/年假/调休：只算"当天请假区间 ∩ 班次上班时段"，休息日不计；没排班的那天按自然日算
    /// （跟 <see cref="AttendanceService.ComputeLeaveHoursForDay"/> 一致）。
    /// 婚假/产假/丧假：按自然日占用整天。
    /// </summary>
    private static async Task<bool> CountedLeaveOverlapsAsync(
        AttendanceDbContext db, int userId,
        DateTime leaveStart, DateTime leaveEnd, LeaveType? leaveType,
        DateTime otStart, DateTime otEnd, CancellationToken ct)
    {
        var natural = AttendanceService.LeaveCountsNaturalDays(leaveType);
        var from = DateOnly.FromDateTime(leaveStart > otStart ? leaveStart : otStart);
        var to   = DateOnly.FromDateTime(leaveEnd   < otEnd   ? leaveEnd   : otEnd);
        if (from > to) return false;

        var shifts = (await db.ShiftAssignments.Include(a => a.ShiftSchedule)
                .Where(a => a.UserId == userId && a.WorkDate >= from && a.WorkDate <= to)
                .ToListAsync(ct))
            .ToDictionary(a => a.WorkDate, a => a.ShiftSchedule);

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var dayStart = d.ToDateTime(TimeOnly.MinValue);
            var dayEnd   = dayStart.AddDays(1);
            var segStart = leaveStart > dayStart ? leaveStart : dayStart;   // 当天计入请假的起点
            var segEnd   = leaveEnd   < dayEnd   ? leaveEnd   : dayEnd;
            if (segStart >= segEnd) continue;

            if (!natural)
            {
                shifts.TryGetValue(d, out var shift);
                if (shift is not null)
                {
                    if (AttendanceService.IsShiftWeeklyRestDay(d, shift)) continue;   // 休息日不占请假
                    var (windowStart, windowEnd) = AttendanceService.ResolveLeaveWindow(d, shift);
                    if (windowStart > segStart) segStart = windowStart;
                    if (windowEnd   < segEnd)   segEnd   = windowEnd;
                    if (segStart >= segEnd) continue;
                }
                // 没排班：按自然日算（跟 ComputeLeaveHoursForDay 的口径一致）
            }

            if (segStart < otEnd && otStart < segEnd) return true;   // 真的重叠
        }
        return false;
    }
}
