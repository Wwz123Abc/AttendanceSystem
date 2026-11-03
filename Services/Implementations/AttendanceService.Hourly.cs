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

    /// <summary>
    /// 计时工里算不算"夜班"：跨天班次，并且是晚上开始（17:00 之后）的。只看 <see cref="ShiftSchedule.IsCrossDay"/> 不够：
    /// "08:30 上班、次日 00:00 下班"这种白班，下班时间 ≤ 上班时间，会被标成跨天班次（2026-10-22 数据核对发现交付部的
    /// 「INN-SZ临时工班」就是这样），如果按夜班算，白天的打卡会被当成无效、工时也会按夜班规则算。
    /// </summary>
    public static bool IsHourlyNightShift(ShiftSchedule? shift) =>
        shift is { IsCrossDay: true } && shift.WorkStartTime >= new TimeOnly(17, 0);

    /// <summary>计时工夜班（排的是跨天班次）：上班日这个钟点（含）之后才有效。</summary>
    public const int HourlyNightEarliestHour = 19;
    /// <summary>计时工夜班：次日这个钟点（不含）之前才有效，下班卡最晚算到这里，再晚的时间不计入工时。
    /// 2026-11-03 起改为次日 18:00（计时工口径表第 5、10 条）；之前的考勤日仍按次日 10:00（<see cref="OldHourlyNightLatestHour"/>）。</summary>
    public const int HourlyNightLatestHour = 18;

    /// <summary>旧口径（2026-11-02 及以前）的夜班次日上限。</summary>
    public const int OldHourlyNightLatestHour = 10;

    /// <summary>新计时工口径的生效日（按考勤日，2026-11-03）。早到不计工时、夜班次日 18:00 上限、夜班饭点按重叠扣、
    /// 夜班漏打截到窗口结束、取整前加 5 分钟宽限，都只对这个日期及以后生效（见 docs/口径/计时工口径_2026-11-03.md）。</summary>
    public static readonly DateOnly NewHourlyRulesFrom = new(2026, 11, 3);

    /// <summary>这个考勤日适不适用新计时工口径。</summary>
    public static bool IsNewHourlyRule(DateOnly workDate) => workDate >= NewHourlyRulesFrom;

    /// <summary>计时工夜班要扣的饭点（都在上班日的次日）：00:00–01:00、05:30–06:00。</summary>
    public static readonly (TimeOnly From, TimeOnly To)[] HourlyNightMealWindows =
        [(new TimeOnly(0, 0), new TimeOnly(1, 0)), (new TimeOnly(5, 30), new TimeOnly(6, 0))];

    public const string HourlyNightInvalidTimeMessage = "夜班计时工的有效打卡时间是上班日 19:00 到次日下班（2026-11-03 起最晚次日 18:00，之前是次日 10:00），现在不在这个时间范围内";

    /// <summary>
    /// 计时工的一次打卡归属哪个考勤日；返回 null 表示这次打卡无效（2026-10-21 起支持夜班）。
    /// 昨天排的是跨天班次（夜班）→ 次日上限之前的打卡算昨天的延续（2026-11-03 起上限 18:00，之前 10:00）；
    /// 今天排的是跨天班次 → 19:00 起才有效；其余（白班/没排班）按原规则：当天 07:00 起有效，00:00–07:00 无效。
    /// </summary>
    public static DateOnly? ResolveHourlyWorkDate(DateTime punchTime, ShiftSchedule? todayShift, ShiftSchedule? prevShift)
    {
        var d = DateOnly.FromDateTime(punchTime);
        // 夜班次日上限按"这次打卡将要归到的考勤日"选：2026-11-03 及以后的考勤日 18:00，之前仍是 10:00。
        // ★ 不能用打卡的日历日判断：切换日（11-03）凌晨/傍晚的卡要归到 11-02，而 11-02 仍该用 10:00 上限；
        //   用日历日会把 11-03 17:00 这种卡按 18:00 判断、错误地归到 11-02（2026-11-03 审查发现）。
        var prevWorkDate = d.AddDays(-1);
        var latestHour = IsNewHourlyRule(prevWorkDate) ? HourlyNightLatestHour : OldHourlyNightLatestHour;
        if (IsHourlyNightShift(prevShift) && punchTime.Hour < latestHour) return prevWorkDate;
        if (IsHourlyNightShift(todayShift)) return punchTime.Hour >= HourlyNightEarliestHour ? d : null;
        return IsHourlyPunchTimeValid(punchTime) ? d : null;
    }

    /// <summary>
    /// 前一天排的夜班"算不算数"：只有前一天确实打过上班卡（上过这个夜班），次日早上的卡才可能是它的下班卡。
    /// 没上班卡（夜班周换白班周，中间那天还挂着夜班、人没来）就当没有这个夜班，按今天的规则判断
    /// （非计时工的夜班续接一直要求昨天有上班卡，这里保持一致）。传给 <see cref="ResolveHourlyWorkDate"/> 的 prevShift 用这个过一遍。
    /// </summary>
    public static ShiftSchedule? EffectivePrevNightShift(ShiftSchedule? prevShift, DateTime? prevClockIn) =>
        IsHourlyNightShift(prevShift) && prevClockIn is null ? null : prevShift;

    /// <summary>打卡无效时给员工看的提示：夜班员工和白班员工的有效时间不一样。</summary>
    public static string HourlyInvalidMessageFor(ShiftSchedule? todayShift, ShiftSchedule? prevShift) =>
        IsHourlyNightShift(todayShift) || IsHourlyNightShift(prevShift) ? HourlyNightInvalidTimeMessage : HourlyInvalidTimeMessage;

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
    /// 计时工的工时：最后一次下班卡 − 有效上班，扣饭点后向下取整到半小时，不按班次标准工时封顶。
    /// <para>2026-11-03 起按新口径（docs/口径/计时工口径_2026-11-03.md）：</para>
    /// ① 有效上班 = max(实际上班卡, 班次应上班时间)，早到不计工时；迟到仍按实际打卡判；
    /// ② 夜班（跨天且上班 ≥17:00）下班最晚算到次日 18:00；
    /// ③ 饭点按"与在岗区间重叠的默认饭点"扣（跨到次日中午/傍晚也照扣），不再只扣夜班那两段；
    /// ④ 夜班漏打午间卡 → 有效下班截到窗口结束（只丢后半班）；白班仍截到窗口开始；
    /// ⑤ 取整前加 5 分钟宽限（与普通正班同一口径）。
    /// 2026-11-02 及以前仍按老口径（早到计入、夜班次日 10:00、固定扣 1.5h、漏打截窗口开始、无宽限），重算历史时结果不变。
    /// </summary>
    public static decimal ComputeHourlyWorkHours(DateOnly workDate, DateTime clockIn, DateTime clockOut, ShiftSchedule? shift, List<MidCheckWindowResult>? midResults)
    {
        var newRule = IsNewHourlyRule(workDate);
        var effIn = clockIn;
        var effOut = clockOut;

        // ① 早到不计工时：有效上班不早于班次应上班时间（没排班时沿用实际打卡）
        if (newRule && shift is not null)
        {
            var shiftStart = ResolveShiftTime(workDate, shift.WorkStartTime, shift);
            if (shiftStart > effIn) effIn = shiftStart;
        }

        // 午间窗口只对"到岗时窗口还没结束"的人生效：窗口结束（含 30 分钟宽限）之后才到岗的人，根本没有机会打这次午间卡，
        // 不能按"漏打"把下午的工时扣掉（2026-10-21：李小勇 10/19 18:04 才到岗，12:00–13:00 的窗口被判漏打，在岗 3 小时被算成 0）
        if (shift is not null && midResults is { Count: > 0 })
            midResults = DropWindowsEndedBeforeArrival(workDate, shift, midResults, clockIn);

        // ④ 漏打处理：前面几段漏打照旧把有效上班顺延到窗口结束；**最后一段**按口径处理——
        //    新口径的夜班截到"窗口结束"（只丢后半班），其它情况仍截到"窗口开始"（原来是 ResolveSecondHalfAbsentBoundary 的行为）。
        //    ★ 用过滤后结果里自带的 WindowStart/WindowEnd，不能再用配置数组的下标：DropWindowsEndedBeforeArrival 会把
        //      "到岗前就结束"的窗口剔掉，过滤后下标和原始窗口对不上（2026-11-02 上线前自查发现）。
        if (shift is not null && midResults is { Count: > 0 })
        {
            foreach (var missedEnd in ResolveMissedNonLastWindowEnds(workDate, shift, midResults))
                if (missedEnd > effIn) effIn = missedEnd;

            var last = midResults[^1];
            if (!last.IsSatisfied)
            {
                var cutAt = newRule && IsHourlyNightShift(shift) ? last.WindowEnd : last.WindowStart;
                var boundary = ResolveShiftTime(workDate, cutAt, shift);
                if (boundary < effOut) effOut = boundary;
            }
        }

        // 夜班（跨天班次）：下班卡最晚算到次日上限（2026-11-03 起 18:00，之前 10:00）
        if (IsHourlyNightShift(shift))
        {
            var latestHour = newRule ? HourlyNightLatestHour : OldHourlyNightLatestHour;
            var nightLatest = workDate.AddDays(1).ToDateTime(new TimeOnly(latestHour, 0));
            if (effOut > nightLatest) effOut = nightLatest;
            if (effOut <= effIn) return 0;
            // ③ 新口径：饭点按默认饭点与在岗区间的重叠扣（跨到次日中午/傍晚也扣），取整前 +5 分钟宽限
            if (newRule) return ComputeRegularWorkHours(effIn, effOut);
            // 旧口径（2026-11-02 及以前）：固定扣次日 00:00–01:00 + 05:30–06:00 两段饭
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
        if (effOut <= effIn) return 0;
        return newRule ? ComputeRegularWorkHours(effIn, effOut) : ComputeWorkHours(effIn, effOut);
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
    /// 打卡先按"归属哪个考勤日"分组（复用打卡规则 <see cref="ResolveHourlyWorkDate"/>：夜班的次日下班卡归上班那天），
    /// 再取这天最早一次为上班、最晚一次为下班（只有一次则没有下班）；记录里已有的上下班时间（补卡补出来的）也一并参与。
    /// 工时按 <see cref="ComputeHourlyWorkHours"/> 算（午间窗口取记录里当时冻结的窗口，多段只取第一段）；
    /// 加班一律清零（计时工没有加班，在岗时间已经全部算进工时）；
    /// 旷工/未打卡/早退的状态改成正常、迟到照常判断；请假/出差/节假日状态和请假小时不动。
    /// <paramref name="dryRun"/>=true 只计算不写库。幂等：重复跑结果一样。
    /// </summary>
    public async Task<List<HourlyRebuildRow>> RebuildHourlyRecordsAsync(
        IReadOnlyCollection<int> groupIds, DateOnly from, DateOnly to, bool dryRun, CancellationToken ct = default,
        IReadOnlyCollection<int>? onlyUserIds = null, bool useShiftWindows = false)
    {
        // onlyUserIds：只重建这几个人；useShiftWindows：午间窗口用"现在排的班次"的窗口，并把记录上的应上/下班时间刷成现在班次的
        // （默认沿用记录里当时冻结的窗口——班次后来被改过/排错时，冻结的窗口本身就是错的，需要这个开关）
        var users = await db.Users.Where(u => u.AttendanceGroupId != null && groupIds.Contains(u.AttendanceGroupId.Value)
                && (onlyUserIds == null || onlyUserIds.Contains(u.Id)))
            .Select(u => new { u.Id, u.EmployeeNo, u.RealName }).ToListAsync(ct);
        var userIds = users.Select(u => u.Id).ToList();
        // 打卡往前多取一天、往后多取两天：夜班（20:00–05:30）的次日下班卡要归到"上班那天"，区间最后一天也不会漏
        var fromDt = from.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var toDt   = to.AddDays(3).ToDateTime(TimeOnly.MinValue);

        var records = (await db.AttendanceRecords.Where(r => userIds.Contains(r.UserId) && r.WorkDate >= from && r.WorkDate <= to).ToListAsync(ct))
            .ToDictionary(r => (r.UserId, r.WorkDate));
        var shifts = (await db.ShiftAssignments.Include(a => a.ShiftSchedule)
                .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= from.AddDays(-1) && a.WorkDate <= to.AddDays(1)).ToListAsync(ct))
            .ToDictionary(a => (a.UserId, a.WorkDate), a => a.ShiftSchedule);

        // ★ 打卡按"归属哪个考勤日"分组（复用打卡时的同一套规则 ResolveHourlyWorkDate），
        //   不能按打卡的日历日分组：夜班的次日下班卡否则会被分到第二天、还被 07:00 的规则过滤掉，
        //   上班那天只剩一张上班卡 → 工时会算成 0（2026-11-03 审查修）。
        //   前一天排夜班且确实上过（有 19 点后的卡）才算续接，避免"排了夜班人没来、次日早上的白班卡被吸到前一天"。
        var rawPunches = await db.AttendancePunches
            .Where(p => userIds.Contains(p.UserId) && p.PunchTime >= fromDt && p.PunchTime < toDt && p.IsValid)
            .Select(p => new { p.UserId, p.PunchTime }).ToListAsync(ct);
        var nightWorkedOn = rawPunches
            .Where(p => p.PunchTime.Hour >= HourlyNightEarliestHour)
            .Select(p => (p.UserId, Day: DateOnly.FromDateTime(p.PunchTime)))
            .ToHashSet();
        var candsByUserDay = new Dictionary<(int UserId, DateOnly Day), List<DateTime>>();
        foreach (var p in rawPunches.OrderBy(x => x.PunchTime))
        {
            var punchDay  = DateOnly.FromDateTime(p.PunchTime);
            var todayShift = shifts.GetValueOrDefault((p.UserId, punchDay));
            var prevShift  = shifts.GetValueOrDefault((p.UserId, punchDay.AddDays(-1)));
            if (prevShift is not null && !nightWorkedOn.Contains((p.UserId, punchDay.AddDays(-1))))
                prevShift = null;   // 前一天排了夜班但没上：按今天的规则判断，不续接
            if (ResolveHourlyWorkDate(p.PunchTime, todayShift, prevShift) is not { } wd || wd < from || wd > to) continue;
            var key = (p.UserId, wd);
            if (!candsByUserDay.TryGetValue(key, out var list)) candsByUserDay[key] = list = [];
            if (!list.Contains(p.PunchTime)) list.Add(p.PunchTime);
        }

        var result = new List<HourlyRebuildRow>();
        foreach (var u in users)
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            records.TryGetValue((u.Id, d), out var rec);
            candsByUserDay.TryGetValue((u.Id, d), out var dayPunches);
            var punches = dayPunches ?? [];
            // 候选时刻：这天的打卡（已按归属日分好）+ 记录上已有的上下班时间（补卡审批只改记录、不一定有打卡流水；
            // 夜班的下班时间本来就在次日，这里不再按"同一天"过滤）
            var cands = new List<DateTime>(punches);
            if (rec?.ClockInTime is { } rci0) cands.Add(rci0);
            if (rec?.ClockOutTime is { } rco0) cands.Add(rco0);
            cands = cands.Distinct().OrderBy(x => x).ToList();
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
            List<(TimeOnly Start, TimeOnly End)> windows = useShiftWindows
                ? (shift?.ParseMidCheckWindows() ?? [])
                : rec?.MidCheckResults.ParseMidCheckResults().Select(w => (w.WindowStart, w.WindowEnd)).ToList() ?? [];
            if (windows.Count == 0 && shift is not null && !useShiftWindows) windows = shift.ParseMidCheckWindows();
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
            if (useShiftWindows && shift is not null)
            {
                rec.ScheduledStartTime = d.ToDateTime(shift.WorkStartTime);
                rec.ScheduledEndTime = shift.IsCrossDay ? d.ToDateTime(shift.WorkEndTime).AddDays(1) : d.ToDateTime(shift.WorkEndTime);
            }
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

    /// <summary>计时工口径（2026-11-03 第 18 条）：夜班员工次日不得排白班。纯判断，排班页和转班审批共用这一处，避免两份规则漂移。
    /// <paramref name="prevEffective"/> / <paramref name="nextEffective"/> 是"最终会生效的"前后一天班次
    /// （同一批排班里的日期，要先把本批要排的班次算进去）。返回给用户看的提示，没冲突返回 null。</summary>
    public static string? FindNightThenDayConflictText(DateOnly day, ShiftSchedule? target, ShiftSchedule? prevEffective, ShiftSchedule? nextEffective, string who)
    {
        if (target is null || !IsNewHourlyRule(day)) return null;
        if (!IsHourlyNightShift(target) && IsHourlyNightShift(prevEffective))
            return $"{who}{day:MM-dd} 排的是白班，但前一天是夜班（20 点后上班的跨天班次）：夜班员工次日不允许排白班（计时工口径 2026-11-03）";
        if (IsHourlyNightShift(target) && nextEffective is not null && !IsHourlyNightShift(nextEffective))
            return $"{who}{day:MM-dd} 排的是夜班，但次日是白班：夜班员工次日不允许排白班（计时工口径 2026-11-03）";
        return null;
    }

    /// <summary>同上，按库里现有排班查前后一天（单条排班 / 转班审批用）。不是计时工、或不在生效期就返回 null。</summary>
    public async Task<string?> FindNightThenDayConflictAsync(int userId, DateOnly day, ShiftSchedule? target, string who, CancellationToken ct = default)
    {
        if (target is null || !IsNewHourlyRule(day)) return null;
        var groupId = await db.Users.Where(u => u.Id == userId).Select(u => u.AttendanceGroupId).FirstOrDefaultAsync(ct);
        if (!IsHourlyOn(await LoadHourlyGroupStartsAsync(), groupId, day)) return null;
        var prev = await db.ShiftAssignments.Where(a => a.UserId == userId && a.WorkDate == day.AddDays(-1))
            .Select(a => a.ShiftSchedule).FirstOrDefaultAsync(ct);
        var next = await db.ShiftAssignments.Where(a => a.UserId == userId && a.WorkDate == day.AddDays(1))
            .Select(a => a.ShiftSchedule).FirstOrDefaultAsync(ct);
        return FindNightThenDayConflictText(day, target, prev, next, who);
    }
}
