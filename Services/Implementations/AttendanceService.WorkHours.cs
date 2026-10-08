using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>工时与状态计算：上下班状态、中途检查、有效上下班时间、休息扣除、请假时长（<see cref="AttendanceService"/> 的一部分）。</summary>
public partial class AttendanceService
{
    /// <summary>
    /// 算上班状态：实际打卡比「应上班时间 + 迟到容忍」还晚就算迟到。没排班、或者今天是这个员工的
    /// 休息日（<paramref name="isRestDay"/>，不管有没有批加班——休息日没有"应上班时间"可比，
    /// 谈不上迟到）一律算正常。out lateMinutes 把迟到分钟数“带出去”给调用者。
    /// </summary>
    /// ★ "应上班时刻"以 <paramref name="workDate"/>（这条考勤记录归属的那一天）为基准推算，不能用
    /// clockIn 打卡那一刻的日期——跨天夜班在次日凌晨打卡/补卡时，打卡当天的日期已经不是 workDate，
    /// 会把应上班时刻算成"次日"，导致差值为负、永远判不出迟到。用法对齐 <see cref="CalcClockOutStatus"/>。
    internal static AttendanceStatus CalcClockInStatus(
        DateOnly workDate, DateTime clockIn, ShiftSchedule? shift, bool isRestDay, out int lateMinutes)
    {
        lateMinutes = 0;
        if (shift is null || isRestDay) return AttendanceStatus.Normal;
        var scheduled = workDate.ToDateTime(shift.WorkStartTime);   // 应上班时刻
        var diff      = (int)(clockIn - scheduled).TotalMinutes;   // 晚了几分钟
        if (diff > shift.LateToleranceMinutes) { lateMinutes = diff; return AttendanceStatus.Late; }
        return AttendanceStatus.Normal;
    }

    /// <summary>
    /// 算下班状态：实际打卡比「应下班时间 − 早退容忍」还早就算早退。夜班的下班时间顺延一天；
    /// 今天是这个员工的休息日（<paramref name="isRestDay"/>）一律算正常，理由同 <see cref="CalcClockInStatus"/>。
    /// out earlyMinutes 把早退分钟数带出去。
    /// ★ "应下班时刻"以 <paramref name="workDate"/>（这条考勤记录归属的那一天，即班次开始的那天）为基准推算，
    /// 不能用 clockOut 打卡那一刻的日期——跨天班次下班时打卡已经是第二天了，
    /// 拿打卡当天的日期再顺延一天会多算出一整天，导致应下班时刻算错。
    /// </summary>
    internal static AttendanceStatus CalcClockOutStatus(
        DateOnly workDate, DateTime clockOut, ShiftSchedule? shift, bool isRestDay, out int earlyMinutes)
    {
        earlyMinutes = 0;
        if (shift is null || isRestDay) return AttendanceStatus.Normal;
        var scheduled = workDate.ToDateTime(shift.WorkEndTime);   // 应下班时刻
        if (shift.IsCrossDay) scheduled = scheduled.AddDays(1);   // 夜班顺延到 workDate 的第二天
        var diff = (int)(scheduled - clockOut).TotalMinutes;      // 早走了几分钟
        if (diff > shift.EarlyLeaveToleranceMinutes) { earlyMinutes = diff; return AttendanceStatus.EarlyLeave; }
        return AttendanceStatus.Normal;
    }

    /// <summary>算实际工时（小时）：上下班时间差，扣掉压到公司统一"不算钱"时段的部分。</summary>
    private decimal CalcWorkHours(DateTime clockIn, DateTime clockOut) => ComputeWorkHours(clockIn, clockOut);

    /// <summary>
    /// 算某天的实际工时（正班），并处理"休息日自己打卡、没有批准的加班申请就不算工时"这条规则：
    /// 员工只是在自己的休息日/没排班的周末跑来打卡，又没有走加班申请审批，这段时间不能绕开审批流程
    /// 凭空算成正班工时；一旦这天已经有批准的加班（OvertimeHours>0，说明公司认可这天需要上班），
    /// 实际打卡时长就正常按班次时间计入工时。★ 本地打卡、补卡/审批回写都调这一个，保证口径一致。
    /// </summary>
    private async Task<decimal> ComputeDailyWorkHoursAsync(
        AttendanceRecord record, DateOnly workDate, DateTime clockIn, DateTime clockOut, ShiftSchedule? shift, int? groupId)
    {
        var (effectiveClockIn, secondHalfBoundary) = await ResolveEffectiveClockInAsync(record, workDate, clockIn, shift);
        // 休息日不计正班工时：没批加班的，打了卡也不算工时；批了加班的，只算加班（加班时长以审批单为准，
        // 由 OvertimeHours 单独记）。以前"有批准的加班就照常算正班工时"，休息日全天加班的人会被
        // "正班 8 小时 + 加班 13.5 小时"重复计算同一段在岗时间（2026-09-28 用户确认改成只算加班）
        if (IsNonCompRestDay(workDate, shift))
            return 0;
        var effectiveClockOut = ClampEffectiveClockOut(workDate, clockOut, shift, secondHalfBoundary);
        return CalcWorkHours(effectiveClockIn, effectiveClockOut);
    }

    /// <summary>
    /// 算"有效上班时间"（供工时计算用）：按班次配置的每一段午间必打卡窗口，查当天有没有打卡落在里面
    /// （没配窗口的班次跳过这步），顺带把每一段的判定结果写回 record.MidCheckResults，
    /// 再交给 <see cref="ClampEffectiveClockIn"/> 统一算出最终的有效上班时间。
    /// </summary>
    private async Task<(DateTime EffectiveClockIn, DateTime? SecondHalfAbsentBoundary)> ResolveEffectiveClockInAsync(
        AttendanceRecord record, DateOnly workDate, DateTime clockIn, ShiftSchedule? shift)
    {
        var results = await LoadMidCheckResultsAsync(record.UserId, workDate, shift);
        if (shift is null || results is null)
        {
            record.MidCheckResults = null;
            return (ClampEffectiveClockIn(workDate, clockIn, shift, []), null);
        }
        record.MidCheckResults = results.FormatMidCheckResults();

        var missedEnds = ResolveMissedNonLastWindowEnds(workDate, shift, results);
        var effectiveClockIn = ClampEffectiveClockIn(workDate, clockIn, shift, missedEnds);
        return (effectiveClockIn, ResolveSecondHalfAbsentBoundary(workDate, shift, results));
    }

    /// <summary>
    /// 查这个人这一天所有打卡（不分类型），算出班次里每一段午间必打卡窗口的命中情况；班次没配窗口返回 null。
    /// 这次打卡本身可能刚 Add 但还没 SaveChanges，数据库还查不到，要单独从 Local 补进去——不然如果正好
    /// 是这次打卡本身落在窗口里，会查不到自己这一条、误判成没满足窗口。
    /// </summary>
    private async Task<List<MidCheckWindowResult>?> LoadMidCheckResultsAsync(int userId, DateOnly workDate, ShiftSchedule? shift)
    {
        var windows = shift?.ParseMidCheckWindows() ?? [];
        if (shift is null || windows.Count == 0) return null;

        var dayPunches = await db.AttendancePunches
            .Where(p => p.UserId == userId
                     && p.PunchTime >= workDate.ToDateTime(TimeOnly.MinValue).AddDays(-1)
                     && p.PunchTime <= workDate.ToDateTime(TimeOnly.MinValue).AddDays(2))
            .Select(p => p.PunchTime)
            .ToListAsync();
        dayPunches.AddRange(db.AttendancePunches.Local.Where(p => p.UserId == userId).Select(p => p.PunchTime));
        return ResolveMidCheckResults(workDate, shift, windows, dayPunches.Distinct().ToList());
    }

    /// <summary>按班次配置的每一段午间窗口，从给定的打卡时刻列表里找出每一段命中的那次打卡（没命中就是 null）。</summary>
    public static List<MidCheckWindowResult> ResolveMidCheckResults(
        DateOnly workDate, ShiftSchedule shift, List<(TimeOnly Start, TimeOnly End)> windows, List<DateTime> punchTimes)
    {
        var results = new List<MidCheckWindowResult>();
        foreach (var w in windows)
        {
            var windowStart = ResolveShiftTime(workDate, w.Start, shift);
            var windowEnd   = ResolveShiftTime(workDate, w.End, shift);
            var hit = punchTimes.Where(t => t >= windowStart && t <= windowEnd)
                .OrderBy(t => t).Cast<DateTime?>().FirstOrDefault();
            results.Add(new MidCheckWindowResult(w.Start, w.End, hit.HasValue ? TimeOnly.FromDateTime(hit.Value) : null));
        }
        return results;
    }

    /// <summary>
    /// 把班次里的某个"钟点"（比如午间必打卡的开始/结束时间）换算成 workDate 当天的具体时刻。
    /// 跨天班次（夜班）要判断这个钟点是在午夜前还是午夜后：比应上班时刻还早，说明已经跨过午夜，
    /// 落在 workDate 的第二天（比如 22:00 上班的夜班，配了 02:00~03:00 的窗口，02:00 早于 22:00，
    /// 就该顺延到第二天凌晨，不能按 workDate 当天的 02:00 算，那样会比上班时间还早，完全不对）。
    /// </summary>
    public static DateTime ResolveShiftTime(DateOnly workDate, TimeOnly time, ShiftSchedule shift)
    {
        var dt = workDate.ToDateTime(time);
        if (shift.IsCrossDay && time < shift.WorkStartTime) dt = dt.AddDays(1);
        return dt;
    }

    /// <summary>
    /// 考勤机同步时，一次"不是上班、也不是刚打完上班卡没多久"的打卡，够不够资格被当成"下班候选"——
    /// 判断依据不是"是否落在配置的午间必打卡窗口内"（这条路以前试过，效果不可靠：员工午休回来
    /// 打卡只要没精确落进窗口，就会被误判成下班，在真正下班打卡之前账号上会显示一段"早退"，
    /// 等真正下班打卡后才被纠正回来，用户能看到这个中间态、会以为系统出错），改成看"离排班的
    /// 应下班时间还有多久"——只有到了应下班时间前 <see cref="ClockOutEligibleHoursBeforeEnd"/> 小时
    /// 以内，才算下班候选；这之前的打卡（不管落不落在午间必打卡窗口里）一律当"午间打卡"处理，
    /// 不碰上下班时间和状态。没排班时不知道应下班时间，只能按老办法直接当下班。
    /// 代价：如果员工真的提前很多（超过这个小时数）就走了、之后再也没打卡，当天会显示"未打卡"
    /// 而不是"早退"——比起员工每天午休回来都被误判"早退"，这个取舍更合理。
    /// </summary>
    public const int ClockOutEligibleHoursBeforeEnd = 2;

    /// <summary>
    /// 这次打卡是不是"晚于班次下班时间"（只对当天没有任何上班卡时有意义，用来判断"当天第一次打卡"该算上班还是下班）。
    /// 设备同步按"当天第一次算上班"处理，但如果员工漏打了早上的上班卡，当天唯一一次打卡是下班时间之后
    /// （比如 22:01、17:30），把它当上班卡会算出几百分钟的迟到——它明显是下班卡。只对非跨天班次判断
    /// （夜班的下班时间在第二天，晚上首次打卡本来就是上班）；没排班、休息日不判断。
    /// </summary>
    public static bool IsFirstPunchAfterShiftEnd(DateOnly workDate, DateTime time, ShiftSchedule? shift, bool isRestDay)
    {
        if (shift is null || isRestDay || shift.IsCrossDay) return false;
        return time >= workDate.ToDateTime(shift.WorkEndTime);
    }

    public static bool IsEligibleClockOutCandidate(DateTime time, DateOnly workDate, ShiftSchedule? shift)
    {
        if (shift is null) return true;
        var scheduledEnd = workDate.ToDateTime(shift.WorkEndTime);
        if (shift.IsCrossDay) scheduledEnd = scheduledEnd.AddDays(1);
        return time >= scheduledEnd.AddHours(-ClockOutEligibleHoursBeforeEnd);
    }

    /// <summary>
    /// 算"有效上班时间"（供工时计算用），规则叠加，谁把时间往后推得更多就用谁：
    /// 1) 不能靠提前打卡多算钱：有效上班时间不早于排班的应上班时间；
    /// 2) 班次配了午间必打卡窗口的，每一段独立判定：当天没有任何打卡落在某一段窗口内，
    ///    视为"这一段之前没上班"，从这段窗口的结束时间起算；配了多段、缺了不止一段的，
    ///    取"影响最大"（结束时间最晚）的那一段，不会因为缺了好几段就反复往后推、越推越多。
    /// ★ 全系统唯一口径：本地打卡、钉钉同步、补卡回写都调这一个，保证结果一致。
    /// </summary>
    public static DateTime ClampEffectiveClockIn(DateOnly workDate, DateTime clockIn, ShiftSchedule? shift, IReadOnlyList<DateTime> missedWindowEnds)
    {
        var effective = clockIn;

        if (shift is not null)
        {
            var scheduledStart = workDate.ToDateTime(shift.WorkStartTime);
            if (scheduledStart > effective) effective = scheduledStart;
        }

        foreach (var windowEnd in missedWindowEnds)
            if (windowEnd > effective) effective = windowEnd;

        return effective;
    }

    /// <summary>
    /// 算"有效下班时间"（供工时计算用）：不能靠晚走多算钱，有效下班时间不晚于排班的应下班时间
    /// （跨天班次顺延到第二天）；提前下班（早退）不受影响，仍按实际下班时间算，正常反映早退少算的工时。
    /// 加班不再从打卡时间估算，只认「加班申请」审批通过后累加到 OvertimeHours 的时长。
    /// <paramref name="secondHalfAbsentBoundary"/>：配了午间打卡的班次，如果时间最晚的那一段午间窗口
    /// 没打上（见 <see cref="ResolveSecondHalfAbsentBoundary"/>），从这段窗口**开始**时间起到下班就不再
    /// 计入工时（窗口本身就是午休时段，本来就不该算钱；相当于下半个班次不算出勤），不影响
    /// AttendanceStatus，也不发旷工提醒/不计入旷工统计。
    /// ★ 全系统唯一口径：本地打卡、钉钉同步、补卡回写都调这一个，保证结果一致。
    /// </summary>
    public static DateTime ClampEffectiveClockOut(DateOnly workDate, DateTime clockOut, ShiftSchedule? shift, DateTime? secondHalfAbsentBoundary = null)
    {
        if (shift is null) return clockOut;
        var scheduledEnd = workDate.ToDateTime(shift.WorkEndTime);
        if (shift.IsCrossDay) scheduledEnd = scheduledEnd.AddDays(1);
        var effective = scheduledEnd < clockOut ? scheduledEnd : clockOut;
        if (secondHalfAbsentBoundary is { } boundary && boundary < effective) effective = boundary;
        return effective;
    }

    /// <summary>
    /// 配了午间打卡窗口的班次，判断"下半个班次算不算旷工（不计工时）"：只看时间最晚的那一段窗口
    /// （不一定是配置里最后一个，取 WindowEnd 最晚的那个），这段没打上就返回它的**窗口开始时间**，
    /// 供 <see cref="ClampEffectiveClockOut"/> 把有效下班时间收窄到这个点，之后（含这段窗口本身、
    /// 也就是午休时段）到实际下班这段都不算工时。
    /// ★ 这里必须用窗口的开始时间，不能用结束时间——窗口本身就是午休/休息时段，午休从来不算工时，
    /// 如果用结束时间当边界，会把"没打卡证明"的这段休息时间也顺带算成了工时，多算钱。用开始时间
    /// 才能保证从午休开始那一刻起（不管是不是真的在休息、还是没回来上班）都不计入，跟"午休本来就
    /// 不算钱"这条基本规则保持一致（发现于 2026-09-17，之前的实现用了结束时间，是个真实 bug）。
    /// 这段打上了，或者班次没配午间窗口，返回 null（不额外限制）。
    /// 只看最晚这一段，不管前面几段有没有漏打——前面漏打已经由 <see cref="ClampEffectiveClockIn"/> 单独顺延处理了。
    /// </summary>
    public static DateTime? ResolveSecondHalfAbsentBoundary(DateOnly workDate, ShiftSchedule? shift, List<MidCheckWindowResult> results)
    {
        if (shift is null || results.Count == 0) return null;
        var lastWindow = results[ResolveLastWindowIndex(results)];
        return lastWindow.IsSatisfied ? null : ResolveShiftTime(workDate, lastWindow.WindowStart, shift);
    }

    /// <summary>
    /// 算"漏打的、且不是最后一段"窗口的结束时间，供 <see cref="ClampEffectiveClockIn"/> 顺延有效
    /// 上班时间用。★ 必须排除最后一段（跟 <see cref="ResolveSecondHalfAbsentBoundary"/> 用
    /// <see cref="ResolveLastWindowIndex"/> 选出的是同一段）——那一段漏打的后果已经单独由
    /// ResolveSecondHalfAbsentBoundary 处理（下半个班次直接不算工时）。之前这里没排除，导致班次
    /// 只配了一段午间窗口（这一段自然也是"最后一段"）时，漏打这一段会同时触发"上班时间顺延到这段
    /// 结束"和"下班时间收窄到这段结束"两条规则，两边都收缩到同一个点，直接把一整天的工时清零——
    /// 本意只是"下半个班次不算"，结果变成"整天不算"，是个真实的工时计算 bug（发现于 2026-09-17 数据核查）。
    /// </summary>
    public static List<DateTime> ResolveMissedNonLastWindowEnds(DateOnly workDate, ShiftSchedule shift, List<MidCheckWindowResult> results)
    {
        if (results.Count == 0) return [];
        var lastIndex = ResolveLastWindowIndex(results);
        return results.Where((r, i) => i != lastIndex && !r.IsSatisfied)
            .Select(r => ResolveShiftTime(workDate, r.WindowEnd, shift)).ToList();
    }

    /// <summary>挑出"最后一段"窗口在列表里的下标：按结束时间最晚排序，若有多段结束时间刚好相同
    /// （班次配置本身少见但没禁止的情况），再按开始时间最晚的排在最后——两个方法都调这一个，
    /// 保证永远认定同一段是"最后一段"。原来两处各自用不同规则挑"最后一段"（这里按 WindowEnd 值
    /// 相等直接排除所有并列的，上面按 OrderBy(...).Last() 只挑一个），结束时间恰好撞在一起时，
    /// 会导致其中一段漏打的窗口两个函数都不处理、也不影响任何计算，等于被悄悄漏掉。</summary>
    private static int ResolveLastWindowIndex(List<MidCheckWindowResult> results) =>
        Enumerable.Range(0, results.Count)
            .OrderBy(i => results[i].WindowEnd).ThenBy(i => results[i].WindowStart)
            .Last();

    /// <summary>
    /// 把工时数规范成"半小时"为最小单位：不足半小时的零头舍去（1.2→1.0，1.7→1.5，1.5 不变）。
    /// 月度报表里所有对外展示/导出的工时合计都过这一道，保证只会出现整数或 x.5，
    /// 和"每日格子舍去小数"是同一个"不足不计"的口径（工资按工时结算，宁少勿多）。
    /// </summary>
    public static decimal FloorToHalf(decimal hours) => Math.Floor(hours * 2) / 2;

    /// <summary>公司统一的"不算钱"时间段——不再按考勤组配置，谁的工作/加班/请假区间压到这几段，
    /// 重叠的那部分一律不计入时长：12:00-13:00（白班午间）、17:30-18:00（中班晚餐）、
    /// 00:00-01:00（夜班宵夜，跨天班次里相当于"午间"的那顿）、05:30-06:00（早班/夜班交接前的早餐）
    /// （2026-09-29 用户确认，替换原来"原始在岗超过 6/9 小时才整段扣 60/30 分钟"的口径——那种"超过
    /// 阈值才扣整段"的算法会出现"6h00m 不扣、6h01m 反而倒扣近 1 小时"的悬崖，多干一分钟工时反而更少）。</summary>
    private static readonly (TimeOnly Start, TimeOnly End)[] UnpaidBreakWindows =
    [
        (new TimeOnly(12, 0), new TimeOnly(13, 0)),
        (new TimeOnly(17, 30), new TimeOnly(18, 0)),
        (new TimeOnly(0, 0), new TimeOnly(1, 0)),
        (new TimeOnly(5, 30), new TimeOnly(6, 0)),
    ];

    /// <summary>算 [start, end) 这段时间里，落在 <see cref="UnpaidBreakWindows"/> 里的分钟数——不管这段
    /// 区间横跨几天、本身多长，只按实际重叠的分钟数算，没有"超过多久才触发"这种门槛，天然不会有悬崖。</summary>
    internal static double ComputeUnpaidBreakOverlapMinutes(DateTime start, DateTime end)
    {
        if (end <= start) return 0;
        double total = 0;
        for (var day = DateOnly.FromDateTime(start.Date); day <= DateOnly.FromDateTime(end.Date); day = day.AddDays(1))
        {
            foreach (var (winStart, winEnd) in UnpaidBreakWindows)
            {
                var overlapStart = Max(day.ToDateTime(winStart), start);
                var overlapEnd   = Min(day.ToDateTime(winEnd), end);
                if (overlapEnd > overlapStart)
                    total += (overlapEnd - overlapStart).TotalMinutes;
            }
        }
        return total;

        static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
        static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    }

    /// <summary>
    /// 纯计算：由上下班时间算实际工时（小时），扣掉压到公司统一"不算钱"时段（<see cref="UnpaidBreakWindows"/>）
    /// 的那部分。
    /// ★ 全系统唯一的工时公式：本地打卡、钉钉同步、补卡回写、月度汇总都调这一个，保证口径一致（工资按工时结算）。
    /// 出口统一按半小时取整（<see cref="FloorToHalf"/>）——之前这里是 2 位小数，跟月度汇总"逐日
    /// 半小时取整再累加"的口径不一致，同一份数据在"我的记录"页会出现日明细 8.37、月合计却按 8.0
    /// 累加，员工自己相加对不上（2026-09-17 复审发现，口径登记表 §2/§5 决定统一到半小时）。
    /// </summary>
    public static decimal ComputeWorkHours(DateTime clockIn, DateTime clockOut)
    {
        var rawMinutes = (decimal)(clockOut - clockIn).TotalMinutes;   // 在岗总分钟（夜班下班在第二天也没问题）
        if (rawMinutes <= 0) return 0;
        var minutes = rawMinutes - (decimal)ComputeUnpaidBreakOverlapMinutes(clockIn, clockOut);
        return FloorToHalf(Math.Max(0, minutes / 60));
    }

    /// <summary>
    /// 算请假区间落在某一天里的时长（小时），供提交申请时的预估总时长、审批通过后逐日回写共用
    /// （★ 全系统唯一口径，两处必须调同一个函数才不会算出两个不一样的数字）。
    /// 跟真实工时公式一样扣公司统一的"不算钱"时段，但封顶在 <paramref name="dailyCapHours"/>（这天排的
    /// 班次的标准工时，没排班传公司默认标准工时）——不能直接把"这一天和请假区间的交集"套用工时公式：
    /// 那个公式是给真实上下班打卡时间设计的，套在跨天请假的"整天"区间上，会把一整晚的睡眠时间也当成
    /// "在岗时长"一起扣，算出一天 22.5 小时这种荒谬数字（发现于 2026-09-17 代码审查）。
    /// 用标准工时封顶后，请一整天假最多算一天的标准工时，符合"请假时长"这个数字本来的业务含义。
    /// </summary>
    public static decimal ComputeLeaveHoursForDay(
        DateOnly day, DateTime leaveStart, DateTime leaveEnd, decimal dailyCapHours, ShiftSchedule? shift = null)
    {
        var (winStart, winEnd) = ResolveLeaveWindow(day, shift);
        var segStart = leaveStart > winStart ? leaveStart : winStart;
        var segEnd   = leaveEnd   < winEnd   ? leaveEnd   : winEnd;
        if (segEnd <= segStart) return 0;
        var raw = ComputeWorkHours(segStart, segEnd);
        return Math.Min(raw, dailyCapHours);
    }

    /// <summary>
    /// 这一天"可以请假"的时间段：这天排了班就取班次的上下班时间（跨天班次下班顺延到第二天）；没排班就沿用自然日
    /// 0:00~24:00（没有班次可参照）。以前一律按自然日切，跨天请假的第一天会一直算到午夜、最后一天从 0 点算起——
    /// 比如"周一 13:30 ~ 周二 12:00"会被算成 2 天（应约 1 天），当天上午已经上的班还会被记成请假、工时清零
    /// （2026-09-24 第 11 轮审查，用户确认改成按班次时间算）。
    /// </summary>
    public static (DateTime Start, DateTime End) ResolveLeaveWindow(DateOnly day, ShiftSchedule? shift)
    {
        if (shift is null)
            return (day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var start = day.ToDateTime(shift.WorkStartTime);
        var end   = day.ToDateTime(shift.WorkEndTime);
        if (shift.IsCrossDay || end <= start) end = end.AddDays(1);
        return (start, end);
    }

    /// <summary>婚假、产假、丧假按自然日计算（休息日、节假日也算请假）；其余假别（事假/病假/年假/调休）只算工作日。</summary>
    public static bool LeaveCountsNaturalDays(LeaveType? leaveType) =>
        leaveType is LeaveType.MarriageLeave or LeaveType.MaternityLeave or LeaveType.BereavementLeave;

    /// <summary>
    /// 这一天跟请假区间是不是有真实交集（不管时长多少，哪怕只有几分钟也算有）。
    /// 跟 <see cref="ComputeLeaveHoursForDay"/> 算出来是不是 0 小时是两回事：后者的 0 可能是
    /// "这一天跟请假区间根本没交集"（比如请假结束时间恰好卡在午夜），也可能是"确实有交集，
    /// 但时长不足半小时、被 <see cref="ComputeWorkHours"/> 最后一步取整成了 0"（比如请假 20 分钟）——
    /// 只有前一种才应该跳过这一天不标记"请假"，后一种如果也跳过，会导致"请了假但没打卡"的短时长
    /// 请假被误判成旷工（发现于 2026-09-18 数据核查，是 09-18 那次"无交集跳过"修复自身的边界缺陷）。
    /// </summary>
    public static bool HasLeaveOverlapForDay(DateOnly day, DateTime leaveStart, DateTime leaveEnd, ShiftSchedule? shift = null)
    {
        var (winStart, winEnd) = ResolveLeaveWindow(day, shift);
        var segStart = leaveStart > winStart ? leaveStart : winStart;
        var segEnd   = leaveEnd   < winEnd   ? leaveEnd   : winEnd;
        return segEnd > segStart;
    }

    /// <summary>这个人这天的"标准工时"：有排班用排的那个班次自己的标准工时，没排班用公司默认标准工时——
    /// 请假半天时用来算"这天最多还能有多少工时额度"，跟 <see cref="ComputeLeaveHoursForDay"/> 的
    /// dailyCapHours 是同一个概念，抽成公共方法避免各个调用点各写一份判断。</summary>
    public static decimal ResolveDailyStandardHours(ShiftSchedule? shift, decimal defaultDailyHours) =>
        shift?.StandardWorkHours ?? defaultDailyHours;

    /// <summary>
    /// 请假当天如果还有真实打卡（半天假、或先打卡后来才补批的假），按"这天标准工时 − 已经批准的
    /// 请假小时数"封顶后结算实际工时——不能超过这个上限，否则会出现"半天假 + 全天工时"这种既算
    /// 请假又重复计酬的情况；如果这天请的是全天假（<paramref name="leaveHours"/> ≥ 标准工时），
    /// 上限自动变成 0，等价于原来"请假当天工时恒为 0"的行为，两种情形用同一个公式覆盖，
    /// 不用分别写"全天/半天"两套判断（2026-09-17 决定支持半天请假，见口径登记表 §4）。
    /// </summary>
    public static decimal ApplyLeaveHoursCap(decimal computedHours, decimal leaveHours, decimal standardHours) =>
        Math.Min(computedHours, Math.Max(0, standardHours - leaveHours));

    /// <summary>把这天的请假小时数折算成"请假天数"：占当天标准工时的比例四舍五入到最近的 0.5 天
    /// （占比 ≥0.75 算 1 天，[0.25, 0.75) 算 0.5 天，&lt;0.25 算 0 天）。供月度汇总的 LeaveDays
    /// 统计用。旧口径是"占比 >0.5 才算 1 天，否则一律算 0.5 天"，导致同样是"半天假"，上午请假
    /// （比如 3.5h/8h=43.75%）算 0.5 天、下午请假（4.5h/8h=56.25%）却算 1 整天——两种半天假因为
    /// 占比刚好卡在 0.5 两侧，天数差一倍；改成就近取整到 0.5 后两者都落在 0.5 天，不再不对称
    /// （2026-09-21）。</summary>
    public static decimal ResolveLeaveDaysFraction(decimal leaveHours, decimal standardHours)
    {
        if (standardHours <= 0) return 0m;
        var ratio = leaveHours / standardHours;
        return ratio >= 0.75m ? 1m : ratio >= 0.25m ? 0.5m : 0m;
    }
}
