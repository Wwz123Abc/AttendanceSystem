using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Services.Implementations;

/// <summary>补卡所需的"当天现状"：班次、已有的上下班卡、前一天的下班卡、下一个上班的边界、当天所有打卡时刻。</summary>
/// <param name="Shift">这天排的班次（没排班为 null）。</param>
/// <param name="ClockIn">当天考勤记录里已有的上班时间。</param>
/// <param name="ClockOut">当天考勤记录里已有的下班时间。</param>
/// <param name="PrevDayClockOut">前一天考勤记录里的下班时间（夜班的下班在这天早上）。</param>
/// <param name="NextBoundary">下一个上班的边界：下一天已有的上班打卡；没有就取下一天排班的"最早可打上班卡"时刻；都没有为 null。</param>
/// <param name="DayPunches">这天前后（前一天到后天）所有原始打卡时刻，用来判断午间窗口有没有命中。</param>
/// <param name="Hourly">这天是不是按计时工模式算（考勤组开了计时工模式且考勤日不早于起始日）：计时工的补卡时间必须落在当天 07:00–23:59。</param>
public sealed record ReplenishmentContext(
    ShiftSchedule? Shift, DateTime? ClockIn, DateTime? ClockOut, DateTime? PrevDayClockOut, DateTime? NextBoundary, List<DateTime> DayPunches,
    bool Hourly = false);

/// <summary>
/// 补卡（上班 / 下班 / 午间）的统一规则：提交申请时校验一遍，审批通过回写时再校验一遍，两边用同一份，口径不会各写各的。
/// 要点：
/// ① 补上班卡：只有比现有上班时间更早才生效；补下班卡：只有比现有下班时间更晚才生效（不覆盖已有的好数据）；
/// ② 午间卡：必须落在班次配置的午间窗口里、而且这一段现在还没命中，只新增一条午间打卡，不动上下班时间；
/// ③ 时间要放在正确的位置：夜班（跨天）班次按班次推算是哪一天；补的上班卡不能早于前一天的下班卡，
///    补的下班卡不能晚于下一个上班的边界；午间卡要夹在上班卡和下班卡之间。
/// </summary>
public static class PunchReplenishmentRules
{
    /// <summary>补卡类型的中文名（上班/下班/午间），用于提示语和列表显示。</summary>
    public static string TypeText(PunchType? type) => type switch
    {
        PunchType.ClockOut => "下班",
        PunchType.MidCheck => "午间",
        _                  => "上班",
    };

    /// <summary>把申请里的"日期 + 钟点"换算成真正的打卡时刻（夜班跨过午夜会落到第二天）。</summary>
    public static DateTime ResolveTime(PunchType type, DateOnly date, TimeOnly time, DateTime? existingClockIn, ShiftSchedule? shift) => type switch
    {
        PunchType.ClockOut => AttendanceService.ResolvePunchReplenishmentClockOut(date, time, existingClockIn, shift),
        PunchType.MidCheck => shift is null ? date.ToDateTime(time) : AttendanceService.ResolveShiftTime(date, time, shift),
        _                  => date.ToDateTime(time),
    };

    /// <summary>读出补卡校验要用的当天现状（直接查库，不依赖考勤服务，测试里用假考勤服务时也能跑）。</summary>
    public static async Task<ReplenishmentContext> LoadAsync(AttendanceDbContext db, int userId, DateOnly date, CancellationToken ct = default)
    {
        var next = date.AddDays(1);
        var prev = date.AddDays(-1);
        var shift = await db.ShiftAssignments
            .Where(a => a.UserId == userId && a.WorkDate == date)
            .Select(a => a.ShiftSchedule).FirstOrDefaultAsync(ct);
        var rec = await db.AttendanceRecords
            .Where(r => r.UserId == userId && r.WorkDate == date)
            .Select(r => new { r.ClockInTime, r.ClockOutTime }).FirstOrDefaultAsync(ct);
        var prevOut = await db.AttendanceRecords
            .Where(r => r.UserId == userId && r.WorkDate == prev)
            .Select(r => r.ClockOutTime).FirstOrDefaultAsync(ct);

        DateTime? nextBoundary = await db.AttendanceRecords
            .Where(r => r.UserId == userId && r.WorkDate == next && r.ClockInTime != null)
            .Select(r => r.ClockInTime).FirstOrDefaultAsync(ct);
        if (nextBoundary is null)
        {
            var nextShift = await db.ShiftAssignments
                .Where(a => a.UserId == userId && a.WorkDate == next)
                .Select(a => a.ShiftSchedule).FirstOrDefaultAsync(ct);
            // 下一天是休息日的班次也算——休息日加班照样可能打卡，但只取"排了班"的最早可打卡时刻作为边界，没排班就不设边界
            if (nextShift is not null)
                nextBoundary = AttendanceService.ResolveShiftTime(next, nextShift.WorkStartTime, nextShift)
                    .AddMinutes(-nextShift.EarliestClockInMinutes);
        }

        var from = date.ToDateTime(TimeOnly.MinValue).AddDays(-1);
        var to   = date.ToDateTime(TimeOnly.MinValue).AddDays(2);
        var punches = await db.AttendancePunches
            .Where(p => p.UserId == userId && p.PunchTime >= from && p.PunchTime <= to)
            .Select(p => p.PunchTime).ToListAsync(ct);

        var groupId = await db.Users.Where(u => u.Id == userId).Select(u => u.AttendanceGroupId).FirstOrDefaultAsync(ct);
        var hourlyFrom = groupId is { } gid
            ? await db.AttendanceGroups.Where(g => g.Id == gid).Select(g => g.HourlyWorkStartDate).FirstOrDefaultAsync(ct)
            : null;
        var hourly = hourlyFrom is { } hf && date >= hf;

        return new ReplenishmentContext(shift, rec?.ClockInTime, rec?.ClockOutTime, prevOut, nextBoundary, punches, hourly);
    }

    /// <summary>
    /// 这张补卡按当前现状能不能生效；不能的返回给员工看的原因（中文），可以的返回 null。
    /// 提交时返回的原因直接拒绝提交；审批回写时，上班/下班卡返回原因表示"这次不生效"（不覆盖已有数据），午间卡返回原因表示不能通过。
    /// </summary>
    /// <param name="allowOverwrite">管理员"强制覆盖"：上班/下班卡不再要求"比现有的更早/更晚"，允许直接改掉已有的时间
    /// （其余的位置检查——不能越过下班卡、前一天下班卡、下一个上班的边界——照样检查）；午间卡不受影响。</param>
    public static string? FindProblem(PunchType type, DateOnly date, DateTime punchAt, ReplenishmentContext ctx, bool allowOverwrite = false)
    {
        // 计时工：没有夜班/跨天，补的时间必须在当天 07:00 到 23:59 之间（不能补到第二天凌晨）
        if (ctx.Hourly && ctx.Shift is { IsCrossDay: true })
        {
            // 夜班计时工：补的时间必须在上班日 19:00 到次日 10:00 之间
            var nightFrom = date.ToDateTime(new TimeOnly(AttendanceService.HourlyNightEarliestHour, 0));
            var nightTo   = date.AddDays(1).ToDateTime(new TimeOnly(AttendanceService.HourlyNightLatestHour, 0));
            if (punchAt < nightFrom || punchAt >= nightTo)
                return $"夜班计时工补卡的时间必须在上班日 {AttendanceService.HourlyNightEarliestHour:00}:00 到次日 {AttendanceService.HourlyNightLatestHour:00}:00 之间，请重新选择时间";
        }
        else if (ctx.Hourly && (DateOnly.FromDateTime(punchAt) != date || !AttendanceService.IsHourlyPunchTimeValid(punchAt)))
            return $"计时工补卡的时间必须在当天 {AttendanceService.HourlyEarliestPunchHour:00}:00 到 23:59 之间，请重新选择时间（不能选第二天）";
        switch (type)
        {
            case PunchType.ClockIn:
                if (!allowOverwrite && ctx.ClockIn is { } ci && punchAt >= ci)
                    return $"这天已经有上班卡（{ci:HH:mm}），补的上班时间必须比它更早才有效。如果要补的是中午那一次打卡，请选择“午间打卡”";
                if (ctx.ClockOut is { } co && punchAt >= co)
                    return $"补的上班时间（{punchAt:HH:mm}）不能晚于这天已有的下班卡（{co:MM-dd HH:mm}）";
                if (ctx.PrevDayClockOut is { } pOut && punchAt <= pOut)
                    return $"补的上班时间（{punchAt:MM-dd HH:mm}）要晚于前一天的下班卡（{pOut:MM-dd HH:mm}），请核对日期和时间";
                return null;

            case PunchType.ClockOut:
                if (ctx.ClockIn is { } inT && punchAt <= inT)
                    return $"补的下班时间（{punchAt:MM-dd HH:mm}）要晚于这天的上班卡（{inT:MM-dd HH:mm}），请核对日期和时间";
                if (!allowOverwrite && ctx.ClockOut is { } oldOut && punchAt <= oldOut)
                    return $"这天已经有下班卡（{oldOut:MM-dd HH:mm}），补的下班时间必须比它更晚才有效。如果要补的是中午那一次打卡，请选择“午间打卡”";
                if (ctx.NextBoundary is { } next && punchAt >= next)
                    return $"补的下班时间（{punchAt:MM-dd HH:mm}）不能晚于下一个上班的时间（{next:MM-dd HH:mm}），请核对日期和时间";
                return null;

            case PunchType.MidCheck:
                return FindMidCheckProblem(date, punchAt, ctx);

            default:
                return "不支持的补卡类型";
        }
    }

    private static string? FindMidCheckProblem(DateOnly date, DateTime punchAt, ReplenishmentContext ctx)
    {
        var windows = ctx.Shift?.ParseMidCheckWindows() ?? [];
        if (ctx.Shift is null)
            return "这天没有排班，不需要补午间打卡";
        if (windows.Count == 0)
            return "这天的班次没有设置午间必打卡，不需要补午间打卡";
        if (ctx.ClockIn is not { } ci)
            return "这天还没有上班卡，请先补上班卡，再补午间打卡";
        if (punchAt <= ci)
            return $"午间打卡时间（{punchAt:HH:mm}）要晚于这天的上班卡（{ci:HH:mm}）";
        if (ctx.ClockOut is { } co && punchAt >= co)
            return $"午间打卡时间（{punchAt:MM-dd HH:mm}）要早于这天的下班卡（{co:MM-dd HH:mm}）";
        if (ctx.NextBoundary is { } next && punchAt >= next)
            return $"午间打卡时间（{punchAt:MM-dd HH:mm}）不能晚于下一个上班的时间（{next:MM-dd HH:mm}）";

        var results = AttendanceService.ResolveMidCheckResults(date, ctx.Shift, windows, ctx.DayPunches);
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var start = AttendanceService.ResolveShiftTime(date, w.Start, ctx.Shift);
            var end   = AttendanceService.MidCheckEffectiveEnd(date, ctx.Shift, windows, i);   // 含窗口结束后 30 分钟的宽限
            if (punchAt < start || punchAt > end) continue;
            return results[i].HitTime is { } hit
                ? $"这个时段（{w.Start:HH\\:mm}–{w.End:HH\\:mm}）已经有午间打卡（{hit:HH\\:mm}），不需要补"
                : null;   // 落在一个还没命中的窗口里，可以补
        }
        var text = string.Join("、", windows.Select(w => $"{w.Start:HH\\:mm}–{w.End:HH\\:mm}"));
        return $"午间打卡时间必须落在班次设置的午间窗口内（{text}，窗口结束后 {AttendanceService.MidCheckGraceMinutes} 分钟内也算）";
    }
}
