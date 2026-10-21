using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 加班时长不能和班次重叠、也不能依赖"排班有没有及时排上"（2026-10-21 用户要求：无论是否排班、延后排班，
/// 系统都不应该把工时统计错误）。事故回顾：朱志方等人 10/1–10/5 提交的 08:30 起的整天加班单，提交时没排班，
/// 重叠检查被跳过，批准后同一段在岗时间既记了 8 小时正班、又记了整天加班。
/// 现在：① 提交检查和审批回写都用"排的班次，没排就用考勤组最常用的日班"（<see cref="EffectiveShift"/>），
/// 加班只记班次上下班时间之外的部分；② 排班补上/改了之后，用 <see cref="ReconcileOvertimeAsync"/> 把已经批准的加班单重新对一遍。
/// </summary>
public partial class AttendanceService
{
    /// <summary>
    /// 纯计算：加班申请的起止时间，去掉和这几个班次上下班时间重叠的部分（休息日的班次不去，休息日整段都是加班），
    /// 剩下的时间扣公司统一的饭点后，向下取整到 0.5 小时。
    /// <paramref name="shifts"/> 是这次加班可能碰到的班次：加班开始那天的班次，以及前一天的跨天班次（夜班下班后接着干）。
    /// </summary>
    public static decimal ComputeOvertimeHoursOutsideShift(
        DateTime start, DateTime end, IReadOnlyList<(DateOnly Day, ShiftSchedule Shift)> shifts, int? groupId = null)
    {
        if (end <= start) return 0;
        var segments = new List<(DateTime S, DateTime E)> { (start, end) };
        foreach (var (day, shift) in shifts)
        {
            if (IsShiftWeeklyRestDay(day, shift)) continue;
            var (winStart, winEnd) = ResolveLeaveWindow(day, shift);
            var next = new List<(DateTime S, DateTime E)>();
            foreach (var (s, e) in segments)
            {
                if (e <= winStart || s >= winEnd) { next.Add((s, e)); continue; }
                if (s < winStart) next.Add((s, winStart));
                if (e > winEnd) next.Add((winEnd, e));
            }
            segments = next;
        }
        decimal minutes = 0;
        foreach (var (s, e) in segments)
            minutes += (decimal)(e - s).TotalMinutes - (decimal)ComputeUnpaidBreakOverlapMinutes(s, e, groupId);
        return FloorToHalf(Math.Max(0, minutes / 60));
    }

    /// <summary>加班单对应的"可能碰到的班次"：开始那天的班次（没排就兜底）+ 前一天排的跨天班次。</summary>
    internal async Task<List<(DateOnly Day, ShiftSchedule Shift)>> LoadOvertimeShiftsAsync(int userId, DateOnly otDate, int? groupId)
    {
        var list = new List<(DateOnly, ShiftSchedule)>();
        if (await EffectiveShift.ForAsync(db, userId, otDate, groupId) is { } today) list.Add((otDate, today));
        var prevDay = otDate.AddDays(-1);
        if (await EffectiveShift.AssignedAsync(db, userId, prevDay) is { IsCrossDay: true } prev) list.Add((prevDay, prev));
        return list;
    }

    /// <summary>对账结果行：一个人一天一行，旧加班时长 → 按班次对过之后的加班时长。</summary>
    public sealed record OvertimeReconcileRow(
        int UserId, string EmployeeNo, string RealName, DateOnly WorkDate, string ShiftName, decimal OldOvertime, decimal NewOvertime, string Forms);

    /// <summary>
    /// 把 [from, to] 这段时间里"已批准的加班单"重新对一遍班次：每张单只记班次上下班时间之外的部分，
    /// 同一个人同一个考勤日的各张单加起来，和考勤记录上的加班时长比——记录上记多了就改小（只减不增：
    /// 记少了的不动，免得覆盖别的原因造成的数）。排班补上/改了之后、或做历史订正时调用。
    /// <paramref name="userIds"/> 为空 = 所有人；<paramref name="dryRun"/>=true 只算不写库。幂等。
    /// </summary>
    public async Task<List<OvertimeReconcileRow>> ReconcileOvertimeAsync(
        IReadOnlyCollection<int>? userIds, DateOnly from, DateOnly to, bool dryRun, CancellationToken ct = default)
    {
        var fromDt = from.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var toDt   = to.AddDays(2).ToDateTime(TimeOnly.MinValue);
        var forms = await db.ApprovalRequests
            .Where(a => a.ApprovalType == ApprovalType.Overtime && a.ApprovalStatus == ApprovalStatus.Approved
                        && a.OvertimeStartTime != null && a.OvertimeEndTime != null
                        && a.OvertimeStartTime >= fromDt && a.OvertimeStartTime < toDt
                        && (userIds == null || userIds.Contains(a.ApplicantUserId)))
            .ToListAsync(ct);
        if (forms.Count == 0) return [];

        var uids = forms.Select(f => f.ApplicantUserId).Distinct().ToList();
        var users = await db.Users.Where(u => uids.Contains(u.Id))
            .Select(u => new { u.Id, u.EmployeeNo, u.RealName, u.AttendanceGroupId }).ToDictionaryAsync(u => u.Id, ct);

        // (人, 考勤日) → 各张单按班次对过之后的时长合计；同时记下每张单对过之后的时长，用来把单子上存的时长一并改成实际记入的数
        var sumByDay = new Dictionary<(int, DateOnly), decimal>();
        var shiftNameByDay = new Dictionary<(int, DateOnly), string>();
        var formHours = new Dictionary<int, decimal>();
        foreach (var f in forms)
        {
            var u = users[f.ApplicantUserId];
            var otStart = f.OvertimeStartTime!.Value;
            var workDate = await ResolveOvertimeWorkDateAsync(f.ApplicantUserId, otStart);
            if (workDate < from || workDate > to) continue;
            var shifts = await LoadOvertimeShiftsAsync(f.ApplicantUserId, DateOnly.FromDateTime(otStart), u.AttendanceGroupId);
            var hours = ComputeOvertimeHoursOutsideShift(otStart, f.OvertimeEndTime!.Value, shifts, u.AttendanceGroupId);
            formHours[f.Id] = hours;
            sumByDay[(f.ApplicantUserId, workDate)] = sumByDay.GetValueOrDefault((f.ApplicantUserId, workDate)) + hours;
            if (shifts.Count > 0) shiftNameByDay[(f.ApplicantUserId, workDate)] = shifts[0].Shift.ShiftName;
        }

        var result = new List<OvertimeReconcileRow>();
        var touchedMonths = new HashSet<(int UserId, int Year, int Month)>();
        foreach (var ((uid, wd), newSum) in sumByDay.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
        {
            var rec = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == uid && r.WorkDate == wd, ct);
            if (rec is null || rec.OvertimeHours <= newSum) continue;   // 只减不增
            var u = users[uid];
            result.Add(new OvertimeReconcileRow(uid, u.EmployeeNo, u.RealName, wd, shiftNameByDay.GetValueOrDefault((uid, wd), ""),
                rec.OvertimeHours, newSum, string.Join("、", forms.Where(f => f.ApplicantUserId == uid && formHours.ContainsKey(f.Id)
                    && DateOnly.FromDateTime(f.OvertimeStartTime!.Value) >= wd.AddDays(-1) && DateOnly.FromDateTime(f.OvertimeStartTime!.Value) <= wd.AddDays(1)).Select(f => f.RequestNo))));
            if (dryRun) continue;
            AppendApprovalNote(rec, $"加班时长已按班次对账：{rec.OvertimeHours:0.##}→{newSum:0.##} 小时（加班只记班次上下班时间之外的部分）");
            rec.OvertimeHours = newSum;
            rec.UpdatedAt = clock.LocalNow();
            touchedMonths.Add((uid, wd.Year, wd.Month));
        }
        if (dryRun || result.Count == 0) return result;

        // 单子上存的时长也改成对过之后的数（只改记录被改小的那些人当天的单子）
        var changedKeys = result.Select(r => (r.UserId, r.WorkDate)).ToHashSet();
        foreach (var f in forms)
            if (formHours.TryGetValue(f.Id, out var h) && f.OvertimeDurationHours != h
                && changedKeys.Any(k => k.UserId == f.ApplicantUserId
                    && DateOnly.FromDateTime(f.OvertimeStartTime!.Value) >= k.WorkDate.AddDays(-1) && DateOnly.FromDateTime(f.OvertimeStartTime!.Value) <= k.WorkDate.AddDays(1)))
                f.OvertimeDurationHours = h;
        await db.SaveChangesAsync(ct);
        foreach (var grp in touchedMonths.GroupBy(t => (t.Year, t.Month)))
            await GenerateMonthlySummaryAsync(grp.Key.Year, grp.Key.Month, grp.Select(t => t.UserId).Distinct().ToList());
        return result;
    }

    /// <summary>排班补上/改了之后调用：把这些人这段日期的已批准加班单重新对一遍班次，返回被改小的人天数。出错只记日志，不影响排班本身。</summary>
    public async Task<int> ReconcileOvertimeAfterScheduleAsync(IReadOnlyCollection<int> userIds, DateOnly from, DateOnly to)
    {
        try
        {
            return (await ReconcileOvertimeAsync(userIds, from, to, dryRun: false)).Count;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "排班变更后对账加班时长失败（不影响排班），用户数 {Count}，{From}~{To}", userIds.Count, from, to);
            return 0;
        }
    }
}
