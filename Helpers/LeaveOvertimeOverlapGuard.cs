using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;

namespace AttendanceSystem.Helpers;

/// <summary>
/// 加班与请假时段重叠校验（2026-11-05 用户要求）：
/// 同一段时间既批了假、又报了加班，会按"请假封顶 + 加班时长"重复算钱。
/// 提交申请时查"另一类已批准的单子"，审批通过回写时再查一次（防止提交时对方还没批的情况）。
/// 只拦"时间真的重叠"——上午请假、晚上加班这种不重叠的照常允许。
/// 出差不参与这条校验（出差期间确实可能有加班）。
/// </summary>
public static class LeaveOvertimeOverlapGuard
{
    /// <summary>有重叠就抛 <see cref="BusinessException"/>（消息给员工/审批人看）；没有重叠正常返回。</summary>
    public static async Task EnsureNoOverlapAsync(
        AttendanceDbContext db, int applicantUserId, DateTime start, DateTime end,
        ApprovalType submittingType, int? excludeRequestId = null, CancellationToken ct = default)
    {
        if (submittingType is not (ApprovalType.Overtime or ApprovalType.Leave)) return;
        var opposite = submittingType == ApprovalType.Overtime ? ApprovalType.Leave : ApprovalType.Overtime;

        var others = await db.ApprovalRequests.AsNoTracking()
            .Where(a => a.ApplicantUserId == applicantUserId
                        && a.ApprovalType == opposite
                        && a.ApprovalStatus == ApprovalStatus.Approved
                        && (excludeRequestId == null || a.Id != excludeRequestId))
            .Select(a => new { a.RequestNo, a.OvertimeStartTime, a.OvertimeEndTime, a.LeaveStartTime, a.LeaveEndTime })
            .ToListAsync(ct);

        foreach (var o in others)
        {
            var (os, oe) = opposite == ApprovalType.Leave
                ? (o.LeaveStartTime, o.LeaveEndTime)
                : (o.OvertimeStartTime, o.OvertimeEndTime);
            if (os is not { } s || oe is not { } e) continue;
            if (s >= end || start >= e) continue;   // 不重叠

            throw new BusinessException(submittingType == ApprovalType.Overtime
                ? $"这段时间已经批准了请假（{o.RequestNo}，{s:MM-dd HH:mm}~{e:MM-dd HH:mm}），同一段不能再报加班，否则会按请假+加班重复算钱；"
                  + "请把加班时间避开请假时段，或先联系文员核对该请假单"
                : $"这段时间已经批准了加班（{o.RequestNo}，{s:MM-dd HH:mm}~{e:MM-dd HH:mm}），同一段不能再请假，否则会按请假+加班重复算钱；"
                  + "请先调整或撤销那张加班单，或把请假时间避开加班时段");
        }
    }
}
