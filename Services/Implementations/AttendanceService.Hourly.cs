using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 计时工模式（<see cref="AttendanceGroup.HourlyWorkStartDate"/>）的公共方法：是不是计时工、打卡类型怎么判、工时怎么算。
/// 所有改动都只在"这个考勤组开启了计时工模式、且考勤日不早于起始日"时才生效，没开的考勤组走原来的逻辑。
/// </summary>
public partial class AttendanceService
{
    /// <summary>计时工每天最早有效打卡时刻（含）：00:00–07:00 之间的打卡无效，防止刷到凌晨。</summary>
    public const int HourlyEarliestPunchHour = 7;

    public const string HourlyInvalidTimeMessage = "计时工 00:00 到 07:00 之间的打卡无效，请 07:00 以后再打卡";

    /// <summary>计时工夜班（排的是跨天班次）：上班日这个钟点（含）之后才有效。</summary>
    public const int HourlyNightEarliestHour = 19;
    /// <summary>计时工夜班：次日这个钟点（不含）之前才有效，下班卡最晚算到这里，再晚的时间不计入工时。</summary>
    public const int HourlyNightLatestHour = 10;

    /// <summary>计时工夜班要扣的饭点（都在上班日的次日）：00:00–01:00、05:30–06:00。</summary>
    public static readonly (TimeOnly From, TimeOnly To)[] HourlyNightMealWindows =
        [(new TimeOnly(0, 0), new TimeOnly(1, 0)), (new TimeOnly(5, 30), new TimeOnly(6, 0))];

    public const string HourlyNightInvalidTimeMessage = "夜班计时工的有效打卡时间是上班日 19:00 到次日 10:00，现在不在这个时间范围内";

    /// <summary>
    /// 计时工的一次打卡归属哪个考勤日；返回 null 表示这次打卡无效（2026-10-21 起支持夜班）。
    /// 昨天排的是跨天班次（夜班）→ 次日 10:00 之前的打卡算昨天的延续；今天排的是跨天班次 → 19:00 起才有效；
    /// 其余（白班/没排班）按原规则：当天 07:00 起有效，00:00–07:00 无效。
    /// </summary>
    public static DateOnly? ResolveHourlyWorkDate(DateTime punchTime, ShiftSchedule? todayShift, ShiftSchedule? prevShift)
    {
        var d = DateOnly.FromDateTime(punchTime);
        if (prevShift is { IsCrossDay: true } && punchTime.Hour < HourlyNightLatestHour) return d.AddDays(-1);
        if (todayShift is { IsCrossDay: true }) return punchTime.Hour >= HourlyNightEarliestHour ? d : null;
        return IsHourlyPunchTimeValid(punchTime) ? d : null;
    }

    /// <summary>
    /// 前一天排的夜班"算不算数"：只有前一天确实打过上班卡（上过这个夜班），次日早上的卡才可能是它的下班卡。
    /// 没上班卡（夜班周换白班周，中间那天还挂着夜班、人没来）就当没有这个夜班，按今天的规则判断
    /// （非计时工的夜班续接一直要求昨天有上班卡，这里保持一致）。传给 <see cref="ResolveHourlyWorkDate"/> 的 prevShift 用这个过一遍。
    /// </summary>
    public static ShiftSchedule? EffectivePrevNightShift(ShiftSchedule? prevShift, DateTime? prevClockIn) =>
        prevShift is { IsCrossDay: true } && prevClockIn is null ? null : prevShift;

    /// <summary>打卡无效时给员工看的提示：夜班员工和白班员工的有效时间不一样。</summary>
    public static string HourlyInvalidMessageFor(ShiftSchedule? todayShift, ShiftSchedule? prevShift) =>
        todayShift is { IsCrossDay: true } || prevShift is { IsCrossDay: true } ? HourlyNightInvalidTimeMessage : HourlyInvalidTimeMessage;

    /// <summary>所有开启了计时工模式的考勤组：考勤组 Id → 起始日。</summary>
    public async Task<Dictionary<int, DateOnly>> LoadHourlyGroupStartsAsync() =>
        await db.AttendanceGroups.Where(g => g.HourlyWorkStartDate != null)
            .ToDictionaryAsync(g => g.Id, g => g.HourlyWorkStartDate!.Value);

    /// <summary>这个考勤组在这个考勤日是不是按计时工模式算。</summary>
    public static bool IsHourlyOn(IReadOnlyDictionary<int, DateOnly> starts, int? groupId, DateOnly workDate) =>
        groupId is { } g && starts.TryGetValue(g, out var from) && workDate >= from;

    public async Task<bool> IsHourlyOnAsync(int? groupId, DateOnly workDate)
    {
        if (groupId is not { } g) return false;
        var from = await db.AttendanceGroups.Where(x => x.Id == g).Select(x => x.HourlyWorkStartDate).FirstOrDefaultAsync();
        return from is { } f && workDate >= f;
    }

    /// <summary>计时工的有效打卡时刻：07:00（含）之后、当天 23:59 之前。</summary>
    public static bool IsHourlyPunchTimeValid(DateTime punchTime) => punchTime.Hour >= HourlyEarliestPunchHour;

    /// <summary>
    /// 计时工的打卡类型（远程打卡页和考勤机同步共用）：当天还没有上班卡 → 上班；落在午间窗口里（含窗口结束后 30 分钟宽限）→ 午间；
    /// 其余都是下班卡（取当天最晚的一次）。计时工下班时间不固定，不能按"应下班前 2 小时才算下班"来判断。
    /// </summary>
    public static PunchType ResolveHourlyPunchType(DateTime punchTime, DateOnly workDate, ShiftSchedule? shift, bool hasClockIn)
    {
        if (!hasClockIn) return PunchType.ClockIn;
        var windows = shift?.ParseMidCheckWindows() ?? [];
        for (var i = 0; i < windows.Count; i++)
            if (punchTime >= ResolveShiftTime(workDate, windows[i].Start, shift!)
                && punchTime <= MidCheckEffectiveEnd(workDate, shift!, windows, i))
                return PunchType.MidCheck;
        return PunchType.ClockOut;
    }

    /// <summary>
    /// 计时工的工时：最后一次下班卡 − 第一次上班卡，扣午饭/晚饭（<see cref="ComputeWorkHours"/> 按默认饭点重叠扣减），
    /// 不按班次标准工时封顶、不把早到晚走裁剪到班次时间；午间窗口漏打：只算窗口开始之前
    /// （跟原来的 <see cref="ResolveSecondHalfAbsentBoundary"/> 同一口径）；当天 23:59:59 之后的时间不算。
    /// </summary>
    public static decimal ComputeHourlyWorkHours(DateOnly workDate, DateTime clockIn, DateTime clockOut, ShiftSchedule? shift, List<MidCheckWindowResult>? midResults)
    {
        var effIn = clockIn;
        var effOut = clockOut;
        // 午间窗口只对"到岗时窗口还没结束"的人生效：窗口结束（含 30 分钟宽限）之后才到岗的人，根本没有机会打这次午间卡，
        // 不能按"漏打"把下午的工时扣掉（2026-10-21：李小勇 10/19 18:04 才到岗，12:00–13:00 的窗口被判漏打，在岗 3 小时被算成 0）
        if (shift is not null && midResults is { Count: > 0 })
            midResults = midResults.Where(w => w.IsSatisfied
                || clockIn < ResolveShiftTime(workDate, w.WindowEnd, shift).AddMinutes(MidCheckGraceMinutes)).ToList();
        if (shift is not null && midResults is { Count: > 0 })
        {
            foreach (var missedEnd in ResolveMissedNonLastWindowEnds(workDate, shift, midResults))
                if (missedEnd > effIn) effIn = missedEnd;
            if (ResolveSecondHalfAbsentBoundary(workDate, shift, midResults) is { } boundary && boundary < effOut)
                effOut = boundary;
        }
        // 夜班（跨天班次）：下班卡最晚算到次日 10:00；扣次日 00:00–01:00（1 小时）和 05:30–06:00（0.5 小时）两段饭
        // （2026-10-21 用户确认：00:00–01:00，再加上系统默认的 05:30–06:00），不再按白班的 12:00–13:00、17:30–18:00 扣；不封顶。
        // 班次配了午间窗口（比如 00:00–01:00）的话，漏打也按计时工统一规则：只算窗口开始之前的工时（上面已经处理）
        if (shift is { IsCrossDay: true })
        {
            var nightLatest = workDate.AddDays(1).ToDateTime(new TimeOnly(HourlyNightLatestHour, 0));
            if (effOut > nightLatest) effOut = nightLatest;
            if (effOut <= effIn) return 0;
            var nextMidnight = workDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
            decimal mealMinutes = 0m;
            foreach (var (mealFrom, mealTo) in HourlyNightMealWindows)
            {
                var mealStart = nextMidnight.Add(mealFrom.ToTimeSpan());
                var mealEnd   = nextMidnight.Add(mealTo.ToTimeSpan());
                var overlapStart = effIn > mealStart ? effIn : mealStart;
                var overlapEnd   = effOut < mealEnd ? effOut : mealEnd;
                if (overlapEnd > overlapStart) mealMinutes += (decimal)(overlapEnd - overlapStart).TotalMinutes;
            }
            return FloorToHalf(Math.Max(0m, ((decimal)(effOut - effIn).TotalMinutes - mealMinutes) / 60m));
        }
        var dayEnd = workDate.ToDateTime(new TimeOnly(23, 59, 59));
        if (effOut > dayEnd) effOut = dayEnd;
        return effOut <= effIn ? 0 : ComputeWorkHours(effIn, effOut);
    }

    /// <summary>
    /// 计时工：上班卡照常判断迟到（只记录，不影响工时）；下班时间不固定，没有早退；没有旷工、缺卡：
    /// 早退/旷工/未打卡的状态改成"正常"，早退分钟数清零（出差、请假、节假日这些审批定的状态不动）。
    /// </summary>
    public static void NormalizeHourlyRecord(AttendanceRecord record)
    {
        record.EarlyLeaveMinutes = 0;
        if (record.AttendanceStatus is AttendanceStatus.EarlyLeave or AttendanceStatus.Absent or AttendanceStatus.NotPunched)
            record.AttendanceStatus = AttendanceStatus.Normal;
    }

    /// <summary>计时工历史重建的结果行（一个人一天一行）：新旧对比，给管理员核对用。</summary>
    public sealed record HourlyRebuildRow(
        int UserId, string EmployeeNo, string RealName, DateOnly WorkDate, string ShiftName,
        DateTime? OldIn, DateTime? OldOut, decimal OldHours, decimal OldOvertime, AttendanceStatus? OldStatus,
        DateTime? NewIn, DateTime? NewOut, decimal NewHours, decimal NewOvertime, AttendanceStatus NewStatus, bool Created)
    {
        public bool Changed => Created || OldIn != NewIn || OldOut != NewOut || OldHours != NewHours || OldOvertime != NewOvertime || OldStatus != NewStatus;
    }

    /// <summary>
    /// 一次性维护：把某几个考勤组在 [from, to] 这段历史，按计时工规则从原始打卡重建考勤记录
    /// （2026-10-20 用户要求：成都鹰诺深圳分公司 9/25–10/19 的记录也按计时工口径重算）。不属于常规流程，由维护工具调用。
    /// 每个人每一天：有效打卡 = 当天 07:00 到 23:59 之间的原始打卡，加上记录里已有的上下班时间（补卡补出来的）
    /// 只要也落在这个时段；上班 = 最早一次，下班 = 最晚一次（只有一次则没有下班）；工时按 <see cref="ComputeHourlyWorkHours"/>
    /// 算（午间窗口取记录里当时冻结的窗口，多段只取第一段）；加班一律清零（计时工没有加班，在岗时间已经全部算进工时）；
    /// 旷工/未打卡/早退的状态改成正常、迟到照常判断；请假/出差/节假日状态和请假小时不动。
    /// <paramref name="dryRun"/>=true 只计算不写库。幂等：重复跑结果一样。
    /// </summary>
    public async Task<List<HourlyRebuildRow>> RebuildHourlyRecordsAsync(
        IReadOnlyCollection<int> groupIds, DateOnly from, DateOnly to, bool dryRun, CancellationToken ct = default)
    {
        var users = await db.Users.Where(u => u.AttendanceGroupId != null && groupIds.Contains(u.AttendanceGroupId.Value))
            .Select(u => new { u.Id, u.EmployeeNo, u.RealName }).ToListAsync(ct);
        var userIds = users.Select(u => u.Id).ToList();
        var fromDt = from.ToDateTime(TimeOnly.MinValue);
        var toDt   = to.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var records = (await db.AttendanceRecords.Where(r => userIds.Contains(r.UserId) && r.WorkDate >= from && r.WorkDate <= to).ToListAsync(ct))
            .ToDictionary(r => (r.UserId, r.WorkDate));
        var punchesByUserDay = (await db.AttendancePunches
                .Where(p => userIds.Contains(p.UserId) && p.PunchTime >= fromDt && p.PunchTime < toDt && p.IsValid)
                .Select(p => new { p.UserId, p.PunchTime }).ToListAsync(ct))
            .GroupBy(p => (p.UserId, DateOnly.FromDateTime(p.PunchTime)))
            .ToDictionary(g => g.Key, g => g.Select(x => x.PunchTime).Distinct().OrderBy(x => x).ToList());
        var shifts = (await db.ShiftAssignments.Include(a => a.ShiftSchedule)
                .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= from && a.WorkDate <= to).ToListAsync(ct))
            .ToDictionary(a => (a.UserId, a.WorkDate), a => a.ShiftSchedule);

        var result = new List<HourlyRebuildRow>();
        foreach (var u in users)
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            records.TryGetValue((u.Id, d), out var rec);
            punchesByUserDay.TryGetValue((u.Id, d), out var rawPunches);
            var punches = (rawPunches ?? []).Where(IsHourlyPunchTimeValid).ToList();
            var cands = new List<DateTime>(punches);
            if (rec?.ClockInTime is { } rci && DateOnly.FromDateTime(rci) == d && IsHourlyPunchTimeValid(rci)) cands.Add(rci);
            if (rec?.ClockOutTime is { } rco && DateOnly.FromDateTime(rco) == d && IsHourlyPunchTimeValid(rco)) cands.Add(rco);
            if (rec is null && cands.Count == 0) continue;

            shifts.TryGetValue((u.Id, d), out var shift);
            DateTime? newIn = null, newOut = null;
            if (cands.Count > 0)
            {
                newIn = cands.Min();
                var latest = cands.Max();
                if (latest > newIn) newOut = latest;
            }

            // 午间窗口：用记录里当时冻结的窗口（班次后来可能改过/停用），多段只取第一段（新规则只有一个午间窗口）
            List<(TimeOnly Start, TimeOnly End)> windows = rec?.MidCheckResults.ParseMidCheckResults()
                    .Select(w => (w.WindowStart, w.WindowEnd)).ToList()
                ?? [];
            if (windows.Count == 0 && shift is not null) windows = shift.ParseMidCheckWindows();
            if (windows.Count > 1) windows = [windows.OrderBy(w => w.Start).First()];
            List<MidCheckWindowResult>? mid = shift is not null && windows.Count > 0
                ? ResolveMidCheckResults(d, shift, windows, punches)
                : null;

            var newHours = newIn is { } i && newOut is { } o ? ComputeHourlyWorkHours(d, i, o, shift, mid) : 0m;

            var oldStatus = rec?.AttendanceStatus;
            var newStatus = oldStatus ?? AttendanceStatus.Normal;
            var newLate = 0;
            if (oldStatus is AttendanceStatus.OnLeave or AttendanceStatus.Holiday or AttendanceStatus.BusinessTrip)
                newLate = rec!.LateMinutes;   // 审批定的状态不动
            else if (newIn is { } inT && shift is not null)
            {
                var st = CalcClockInStatus(d, inT, shift, false, out newLate);
                newStatus = st == AttendanceStatus.Late ? AttendanceStatus.Late : AttendanceStatus.Normal;
            }
            else newStatus = AttendanceStatus.Normal;

            var row = new HourlyRebuildRow(u.Id, u.EmployeeNo, u.RealName, d, shift?.ShiftName ?? "",
                rec?.ClockInTime, rec?.ClockOutTime, rec?.ActualWorkHours ?? 0m, rec?.OvertimeHours ?? 0m, oldStatus,
                newIn, newOut, newHours, 0m, newStatus, rec is null);
            result.Add(row);
            if (dryRun || !row.Changed) continue;

            if (rec is null)
            {
                rec = new AttendanceRecord { UserId = u.Id, WorkDate = d };
                db.AttendanceRecords.Add(rec);
                records[(u.Id, d)] = rec;
            }
            rec.ClockInTime = newIn; rec.ClockOutTime = newOut;
            rec.ActualWorkHours = newHours; rec.OvertimeHours = 0;
            rec.AttendanceStatus = newStatus; rec.LateMinutes = newLate; rec.EarlyLeaveMinutes = 0;
            rec.MidCheckResults = mid?.FormatMidCheckResults();
            AppendApprovalNote(rec, "已按计时工规则重算（2026-10-20）");
            rec.UpdatedAt = clock.LocalNow();
        }

        if (!dryRun)
        {
            await db.SaveChangesAsync(ct);
            foreach (var g in result.Where(r => r.Changed).GroupBy(r => (r.WorkDate.Year, r.WorkDate.Month)))
                await GenerateMonthlySummaryAsync(g.Key.Year, g.Key.Month, g.Select(r => r.UserId).Distinct().ToList());
        }
        return result;
    }
}
