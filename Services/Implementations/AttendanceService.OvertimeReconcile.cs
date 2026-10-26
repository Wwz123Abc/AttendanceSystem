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
        DateTime start, DateTime end, IReadOnlyList<(DateOnly Day, ShiftSchedule Shift)> shifts, int? groupId = null,
        IReadOnlyCollection<DateOnly>? fallbackDays = null)
    {
        if (end <= start) return 0;
        var segments = new List<(DateTime S, DateTime E)> { (start, end) };
        foreach (var (day, shift) in shifts)
        {
            // 兜底班次（那天没排班）：是不是休息日和正班那边一致，只按周六周日；排了班的按班次自己配置的休息日
            var isRest = fallbackDays?.Contains(day) == true ? IsShiftWeeklyRestDay(day, null) : IsShiftWeeklyRestDay(day, shift);
            if (isRest) continue;
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
        // 饭点用加班开始那天的班次的设置（班次没设就用考勤组的）；这天没有班次就用列表里第一个
        int? breakShiftId = null;
        foreach (var (day, sh) in shifts)
            if (day == DateOnly.FromDateTime(start)) { breakShiftId = sh.Id; break; }
        if (breakShiftId is null && shifts.Count > 0) breakShiftId = shifts[0].Shift.Id;
        decimal minutes = 0;
        foreach (var (s, e) in segments)
            minutes += (decimal)(e - s).TotalMinutes - (decimal)ComputeUnpaidBreakOverlapMinutes(s, e, groupId, breakShiftId);
        return FloorToHalf(Math.Max(0, minutes / 60));
    }

    /// <summary>加班单对应的"可能碰到的班次"：开始那天的班次（没排就兜底：他最近排的班 → 考勤组常用日班）+ 前一天排的跨天班次。fallbackDays = 其中属于兜底的日期。</summary>
    internal async Task<(List<(DateOnly Day, ShiftSchedule Shift)> Shifts, HashSet<DateOnly> FallbackDays)> LoadOvertimeShiftsAsync(
        int userId, DateOnly otDate, int? groupId, Dictionary<int, ShiftSchedule?>? groupDefaultCache = null)
    {
        var list = new List<(DateOnly, ShiftSchedule)>();
        var fallback = new HashSet<DateOnly>();
        var (todayShift, isFallback) = await EffectiveShift.ResolveAsync(db, userId, otDate, groupId, default, groupDefaultCache);
        if (todayShift is not null) { list.Add((otDate, todayShift)); if (isFallback) fallback.Add(otDate); }
        var prevDay = otDate.AddDays(-1);
        if (await EffectiveShift.AssignedAsync(db, userId, prevDay) is { IsCrossDay: true } prev) list.Add((prevDay, prev));
        return (list, fallback);
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
        IReadOnlyCollection<int>? userIds, DateOnly from, DateOnly to, bool dryRun, CancellationToken ct = default,
        bool includeUnderRecorded = false)
    {
        // includeUnderRecorded（只在试算时有意义）：把"记录上的加班比单子对账后的合计还少"的人天也列出来（正常对账只减不增，
        // 看不到少记的——2026-10-22 查谢振波 10/3 发现 10/20 的"夜班延续订正"把晚上那张 11 小时的单子误清零）
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

        // 批量对账要对几千张单，班次一次性预加载（人 × 日期 → 班次），不能逐张单查库
        var shiftByUserDay = (await db.ShiftAssignments
                .Where(a => uids.Contains(a.UserId) && a.WorkDate >= from.AddDays(-3) && a.WorkDate <= to.AddDays(3))
                .Select(a => new { a.UserId, a.WorkDate, a.ShiftSchedule }).ToListAsync(ct))
            .ToDictionary(a => (a.UserId, a.WorkDate), a => a.ShiftSchedule);
        var groupDefaultCache = new Dictionary<int, ShiftSchedule?>();
        foreach (var gid in users.Values.Select(u => u.AttendanceGroupId).Where(g => g.HasValue).Select(g => g!.Value).Distinct())
            await EffectiveShift.GroupDefaultAsync(db, gid, ct, groupDefaultCache);
        // 兜底用"这个人最近一次实际排的班"：先在预加载的窗口里找离这天最近的；窗口里一次都没排过的人再单独查一次（每人只查一次）
        var shiftsByUser = shiftByUserDay.GroupBy(k => k.Key.Item1).ToDictionary(g => g.Key, g => g.Select(x => (Day: x.Key.Item2, Shift: x.Value)).OrderBy(x => x.Day).ToList());
        var nearestOutside = new Dictionary<int, ShiftSchedule?>();
        async Task<ShiftSchedule?> NearestAsync(int uid, DateOnly day)
        {
            if (shiftsByUser.TryGetValue(uid, out var own) && own.Count > 0)
            {
                var before = own.LastOrDefault(x => x.Day < day);
                if (before.Shift is not null) return before.Shift;
                var after = own.FirstOrDefault(x => x.Day > day);
                if (after.Shift is not null) return after.Shift;
            }
            if (!nearestOutside.TryGetValue(uid, out var outside))
                nearestOutside[uid] = outside = await EffectiveShift.NearestAssignedAsync(db, uid, day, ct);
            return outside;
        }

        // (人, 考勤日) → 各张单按班次对过之后的时长合计；同时记下每张单对过之后的时长，用来把单子上存的时长一并改成实际记入的数
        var sumByDay = new Dictionary<(int, DateOnly), decimal>();
        var shiftNameByDay = new Dictionary<(int, DateOnly), string>();
        var formHours = new Dictionary<int, decimal>();
        var formWorkDate = new Dictionary<int, DateOnly>();                      // 每张单归到哪个考勤日
        var reductionByDay = new Dictionary<(int, DateOnly), decimal>();        // 这天的单子按班次对过之后"少算了多少"（只算少的）
        foreach (var f in forms)
        {
            var u = users[f.ApplicantUserId];
            var otStart = f.OvertimeStartTime!.Value;
            var otDate = DateOnly.FromDateTime(otStart);
            shiftByUserDay.TryGetValue((f.ApplicantUserId, otDate.AddDays(-1)), out var yShift);
            shiftByUserDay.TryGetValue((f.ApplicantUserId, otDate), out var assignedToday);
            var workDate = ResolveOvertimeWorkDate(otStart, yShift, assignedToday);
            if (workDate < from || workDate > to) continue;

            var shifts = new List<(DateOnly, ShiftSchedule)>();
            var fallbackDays = new HashSet<DateOnly>();
            var todayShift = assignedToday;
            if (todayShift is null)   // 没排班：他最近一次实际排的班 → 考勤组常用日班
            {
                todayShift = await NearestAsync(f.ApplicantUserId, otDate)
                             ?? (u.AttendanceGroupId is { } g ? groupDefaultCache.GetValueOrDefault(g) : null);
                if (todayShift is not null) fallbackDays.Add(otDate);
            }
            if (todayShift is not null) shifts.Add((otDate, todayShift));
            if (yShift is { IsCrossDay: true }) shifts.Add((otDate.AddDays(-1), yShift));

            var hours = ComputeOvertimeHoursOutsideShift(otStart, f.OvertimeEndTime!.Value, shifts, u.AttendanceGroupId, fallbackDays);
            formHours[f.Id] = hours;
            formWorkDate[f.Id] = workDate;
            var oldDuration = f.OvertimeDurationHours ?? 0m;
            if (hours < oldDuration) reductionByDay[(f.ApplicantUserId, workDate)] = reductionByDay.GetValueOrDefault((f.ApplicantUserId, workDate)) + (oldDuration - hours);
            sumByDay[(f.ApplicantUserId, workDate)] = sumByDay.GetValueOrDefault((f.ApplicantUserId, workDate)) + hours;
            if (shifts.Count > 0) shiftNameByDay[(f.ApplicantUserId, workDate)] = shifts[0].Item2.ShiftName + (assignedToday is null ? "（兜底）" : "");
        }

        var recordByUserDay = (await db.AttendanceRecords
                .Where(r => uids.Contains(r.UserId) && r.WorkDate >= from && r.WorkDate <= to).ToListAsync(ct))
            .ToDictionary(r => (r.UserId, r.WorkDate));

        var result = new List<OvertimeReconcileRow>();
        var touchedMonths = new HashSet<(int UserId, int Year, int Month)>();
        foreach (var ((uid, wd), newSum) in sumByDay.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
        {
            recordByUserDay.TryGetValue((uid, wd), out var rec);
            if (includeUnderRecorded && dryRun && (rec?.OvertimeHours ?? 0m) + 0.01m < newSum)
            {
                var uu = users[uid];
                result.Add(new OvertimeReconcileRow(uid, uu.EmployeeNo, uu.RealName, wd, shiftNameByDay.GetValueOrDefault((uid, wd), ""),
                    rec?.OvertimeHours ?? 0m, newSum, string.Join("、", forms.Where(f => f.ApplicantUserId == uid && formHours.ContainsKey(f.Id)
                        && DateOnly.FromDateTime(f.OvertimeStartTime!.Value) >= wd.AddDays(-1) && DateOnly.FromDateTime(f.OvertimeStartTime!.Value) <= wd.AddDays(1)).Select(f => f.RequestNo))));
                continue;
            }
            if (rec is null || rec.OvertimeHours <= newSum) continue;   // 只减不增
            // 只扣掉"单子按班次对过之后少算的那部分"，不是直接赋值成单子合计：记录上多出来的部分如果是手工订正/历史导入的，
            // 不能被夜里或排班页保存时顺手抹掉（2026-10-26 审查）；单子本身没变的天，记录保持原样
            var newValue = Math.Max(newSum, rec.OvertimeHours - reductionByDay.GetValueOrDefault((uid, wd)));
            if (!dryRun && newValue >= rec.OvertimeHours) continue;   // 真改时没有"单子少算的那部分"就不动；试算/数据核对仍然报出"记录比单子多"，让人看
            var u = users[uid];
            result.Add(new OvertimeReconcileRow(uid, u.EmployeeNo, u.RealName, wd, shiftNameByDay.GetValueOrDefault((uid, wd), ""),
                rec.OvertimeHours, dryRun ? newSum : newValue, string.Join("、", forms.Where(f => f.ApplicantUserId == uid && formHours.ContainsKey(f.Id)
                    && DateOnly.FromDateTime(f.OvertimeStartTime!.Value) >= wd.AddDays(-1) && DateOnly.FromDateTime(f.OvertimeStartTime!.Value) <= wd.AddDays(1)).Select(f => f.RequestNo))));
            if (dryRun) continue;
            AppendApprovalNote(rec, $"加班时长已按班次对账：{rec.OvertimeHours:0.##}→{newValue:0.##} 小时（加班只记班次上下班时间之外的部分）");
            rec.OvertimeHours = newValue;
            rec.UpdatedAt = clock.LocalNow();
            touchedMonths.Add((uid, wd.Year, wd.Month));
        }
        if (dryRun || result.Count == 0) return result;

        // 单子上存的时长也改成对过之后的数（只改记录被改小的那些人当天的单子）
        var changedKeys = result.Select(r => (r.UserId, r.WorkDate)).ToHashSet();
        foreach (var f in forms)
            // 只改"归到被改小的这一天"、且按班次对完确实比单子上存的少的单子（只减不增），并在申请理由后留一句说明
            if (formHours.TryGetValue(f.Id, out var h) && h < (f.OvertimeDurationHours ?? 0m)
                && formWorkDate.TryGetValue(f.Id, out var fwd) && changedKeys.Contains((f.ApplicantUserId, fwd)))
            {
                var mark = $"｜{clock.LocalNow():yyyy-MM-dd} 按班次对账：{f.OvertimeDurationHours:0.##}→{h:0.##} 小时";
                f.Reason = ((f.Reason ?? "") + mark) is { Length: > 1000 } longer ? longer[..1000] : (f.Reason ?? "") + mark;
                f.OvertimeDurationHours = h;
                f.UpdatedAt = clock.LocalNow();
            }
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
