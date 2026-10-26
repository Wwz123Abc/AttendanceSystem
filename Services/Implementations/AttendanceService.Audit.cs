using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 数据核对（2026-10-22，用户要求："确保已经正常排班的员工，正常打卡、补卡、申请加班时都能正确计算工时、输出报表，不要再出错"）：
/// 把一段日期里每条考勤记录，按当前规则从原始打卡、班次、午间窗口、加班单重新算一遍，和系统里存的数对比，
/// 只读、不改任何数据。工时/加班怎么算的口径只有一处（<see cref="ComputeWorkHours"/> 一系列函数），这里直接复用，
/// 所以"核对出来对得上"就说明存库的数和现行规则一致；对不上的就是疑点。
/// 疑点类型：工时与打卡不符 · 加班多记/少记 · 有打卡但没排班 · 排班类型与打卡时间对不上 · 有打卡却记旷工 · 上下班时间倒挂。
/// </summary>
public partial class AttendanceService
{
    public const string AuditHours = "工时与打卡不符";
    public const string AuditOtOver = "加班多记";
    public const string AuditOtUnder = "加班少记";
    public const string AuditNoShift = "有打卡但没排班";

    /// <summary>这些考勤组的"有打卡但没排班"不算疑点（2026-10-26 用户确认：深圳GA考勤组长期不排班，数据核对页、每晚核对通知都忽略）。</summary>
    public static readonly string[] NoShiftIgnoredGroups = ["深圳GA考勤组"];
    public const string AuditShiftMismatch = "排班类型与打卡时间对不上";
    public const string AuditAbsentWithPunch = "有打卡却记旷工";
    public const string AuditInverted = "上下班时间倒挂";

    /// <summary>
    /// 纯计算：按当前规则，这条记录的正班工时应该是多少（普通班次 / 计时工 / 夜班计时工 / 请假封顶，都走和打卡结算同一套函数）。
    /// 只用于核对，需要记录有上下班时间且已排班；返回 null 表示这条记录不在核对范围（没有完整上下班卡）。
    /// </summary>
    public static decimal? ExpectedWorkHoursForAudit(
        AttendanceRecord r, ShiftSchedule shift, bool hourly, IReadOnlyList<DateTime> punchTimes, decimal defaultDailyHours)
    {
        if (r.ClockInTime is not { } ci || r.ClockOutTime is not { } co || co <= ci) return null;
        var windows = shift.ParseMidCheckWindows();
        var results = windows.Count > 0 ? ResolveMidCheckResults(r.WorkDate, shift, windows, punchTimes.ToList()) : null;

        if (hourly) return ComputeHourlyWorkHours(r.WorkDate, ci, co, shift, results);
        if (IsNonCompRestDay(r.WorkDate, shift)) return 0m;

        var effective = results is null ? [] : DropWindowsEndedBeforeArrival(r.WorkDate, shift, results, ci);
        var missedEnds = ResolveMissedNonLastWindowEnds(r.WorkDate, shift, effective);
        var boundary = ResolveSecondHalfAbsentBoundary(r.WorkDate, shift, effective);
        var hours = ComputeWorkHours(ClampEffectiveClockIn(r.WorkDate, ci, shift, missedEnds), ClampEffectiveClockOut(r.WorkDate, co, shift, boundary));
        return r.AttendanceStatus == AttendanceStatus.OnLeave
            ? ApplyLeaveHoursCap(hours, r.LeaveHours, ResolveDailyStandardHours(shift, defaultDailyHours))
            : hours;
    }

    /// <summary>重算结果行：一个人一天一行，旧工时 → 按当前班次重算后的工时。</summary>
    public sealed record HoursRecalcRow(int UserId, string EmployeeNo, string RealName, DateOnly WorkDate, string ShiftName, decimal OldHours, decimal NewHours);

    /// <summary>
    /// 排班补上/改了之后，把这些人这段日期"有上下班卡"的记录的正班工时，按当天排的班次重新算一遍
    /// （2026-10-22：员工当时没排班，打卡结算时没有班次可套——不封顶、不套午间窗口；后来补排了班，工时却没人重算，
    /// 同一段打卡在"当时没排班"和"排了班"两种情况下算出的工时不一样，数据核对里查出 237 条工时与打卡不符）。
    /// 只改工时和午间命中显示，不动状态/迟到早退/加班/请假；出差、节假日跳过；没排班的日子跳过。
    /// <paramref name="userIds"/> 为空 = 所有人；<paramref name="dryRun"/>=true 只算不写库。幂等。
    /// </summary>
    public async Task<List<HoursRecalcRow>> RecalcHoursForScheduledAsync(
        IReadOnlyCollection<int>? userIds, DateOnly from, DateOnly to, bool dryRun, CancellationToken ct = default, bool onlyUnfrozen = false)
    {
        var result = new List<HoursRecalcRow>();
        var records = await db.AttendanceRecords.Include(r => r.User)
            .Where(r => r.WorkDate >= from && r.WorkDate <= to && r.ClockInTime != null && r.ClockOutTime != null
                        && r.AttendanceStatus != AttendanceStatus.BusinessTrip && r.AttendanceStatus != AttendanceStatus.Holiday
                        && (userIds == null || userIds.Contains(r.UserId)))
            .ToListAsync(ct);
        if (records.Count == 0) return result;

        var uids = records.Select(r => r.UserId).Distinct().ToList();
        var assignList = await db.ShiftAssignments.Include(a => a.ShiftSchedule)
                .Where(a => uids.Contains(a.UserId) && a.WorkDate >= from && a.WorkDate <= to).ToListAsync(ct);
        var shiftByUserDay = assignList.ToDictionary(a => (a.UserId, a.WorkDate), a => a.ShiftSchedule);
        var assignCreatedAt = assignList.ToDictionary(a => (a.UserId, a.WorkDate), a => a.CreatedAt);
        var fromDt = from.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var toDt = to.AddDays(3).ToDateTime(TimeOnly.MinValue);
        var punchesByUser = (await db.AttendancePunches
                .Where(p => uids.Contains(p.UserId) && p.IsValid && p.PunchTime >= fromDt && p.PunchTime < toDt)
                .Select(p => new { p.UserId, p.PunchTime }).ToListAsync(ct))
            .GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.PunchTime).Distinct().ToList());
        var hourlyStarts = await LoadHourlyGroupStartsAsync();
        var defaultDaily = appOptions.Value.DefaultDailyWorkHours;

        var touchedMonths = new HashSet<(int UserId, int Year, int Month)>();
        foreach (var r in records)
        {
            if (!shiftByUserDay.TryGetValue((r.UserId, r.WorkDate), out var shift)) continue;
            // 每晚自动订正只处理"结算时还没排班、后来补排了班"的记录：这种记录上没有冻结的应上班时间和午间结果。
            // 有冻结数据的记录是按当时的班次设置结算的，管理员后来改了班次设置不该被夜里悄悄套到历史上
            // （项目规则：改班次不回头动历史，勾了"同时重算历史工时"才重算；2026-10-26 审查）
            // 判断"事后补排班"：排班记录是在考勤记录最后一次结算之后才建的（考勤机同步的记录不写冻结字段，
            // 靠有没有冻结字段分不出来，会把班次设置的修改套到历史上，2026-10-26 第三轮审查）
            if (onlyUnfrozen && !(assignCreatedAt.GetValueOrDefault((r.UserId, r.WorkDate)) > r.UpdatedAt)) continue;
            var hourly = IsHourlyOn(hourlyStarts, r.User.AttendanceGroupId, r.WorkDate);
            var punches = punchesByUser.GetValueOrDefault(r.UserId) ?? [];
            if (ExpectedWorkHoursForAudit(r, shift, hourly, punches, defaultDaily) is not { } expected) continue;
            if (Math.Abs(expected - r.ActualWorkHours) < 0.01m) continue;
            result.Add(new HoursRecalcRow(r.UserId, r.User.EmployeeNo, r.User.RealName, r.WorkDate, shift.ShiftName, r.ActualWorkHours, expected));
            if (dryRun) continue;
            AppendApprovalNote(r, $"补排班后工时已按班次重算：{r.ActualWorkHours:0.##}→{expected:0.##}");
            r.ActualWorkHours = expected;
            var windows = shift.ParseMidCheckWindows();
            r.MidCheckResults = windows.Count > 0 ? ResolveMidCheckResults(r.WorkDate, shift, windows, punches).FormatMidCheckResults() : null;
            r.UpdatedAt = clock.LocalNow();
            touchedMonths.Add((r.UserId, r.WorkDate.Year, r.WorkDate.Month));
        }
        if (dryRun || result.Count == 0) return result;
        await db.SaveChangesAsync(ct);
        foreach (var grp in touchedMonths.GroupBy(t => (t.Year, t.Month)))
            await GenerateMonthlySummaryAsync(grp.Key.Year, grp.Key.Month, grp.Select(t => t.UserId).Distinct().ToList());
        return result;
    }

    /// <summary>排班补上/改了之后调用：重算这些人这段日期的正班工时，返回被改的人天数。出错只记日志，不影响排班本身。</summary>
    public async Task<int> RecalcHoursAfterScheduleAsync(IReadOnlyCollection<int> userIds, DateOnly from, DateOnly to, bool onlyUnfrozen = false)
    {
        try { return (await RecalcHoursForScheduledAsync(userIds, from, to, dryRun: false, default, onlyUnfrozen)).Count; }
        catch (Exception ex)
        {
            logger.LogError(ex, "排班变更后重算正班工时失败（不影响排班），用户数 {Count}，{From}~{To}", userIds.Count, from, to);
            return 0;
        }
    }

    /// <summary>
    /// 核对 [from, to] 这段日期。返回所有疑点（按严重程度、人、日期排好序）。只读。
    /// 范围：需要考勤的员工（免考勤的正式工不看），出差/节假日记录不看（工时由审批流程定）。
    /// </summary>
    public async Task<List<AuditIssue>> AuditAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var issues = new List<AuditIssue>();
        var records = await db.AttendanceRecords
            .Include(r => r.User).ThenInclude(u => u.AttendanceGroup)
            .Where(r => r.WorkDate >= from && r.WorkDate <= to
                        && r.User.Role == UserRole.Employee && !r.User.IsAttendanceExempt
                        && r.AttendanceStatus != AttendanceStatus.BusinessTrip && r.AttendanceStatus != AttendanceStatus.Holiday)
            .ToListAsync(ct);
        if (records.Count == 0) return issues;

        var userIds = records.Select(r => r.UserId).Distinct().ToList();
        var shiftByUserDay = (await db.ShiftAssignments.Include(a => a.ShiftSchedule)
                .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= from.AddDays(-1) && a.WorkDate <= to)
                .ToListAsync(ct))
            .ToDictionary(a => (a.UserId, a.WorkDate), a => a.ShiftSchedule);
        var fromDt = from.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var toDt = to.AddDays(3).ToDateTime(TimeOnly.MinValue);
        var punchesByUser = (await db.AttendancePunches
                .Where(p => userIds.Contains(p.UserId) && p.IsValid && p.PunchTime >= fromDt && p.PunchTime < toDt)
                .Select(p => new { p.UserId, p.PunchTime }).ToListAsync(ct))
            .GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.PunchTime).Distinct().ToList());
        var hourlyStarts = await LoadHourlyGroupStartsAsync();
        var defaultDaily = appOptions.Value.DefaultDailyWorkHours;

        foreach (var r in records)
        {
            var u = r.User;
            AuditIssue Make(string type, string severity, string detail, decimal? stored = null, decimal? expected = null) =>
                new(u.Id, u.EmployeeNo, u.RealName, u.AttendanceGroup?.GroupName, r.WorkDate, type, severity, detail, stored, expected);

            shiftByUserDay.TryGetValue((r.UserId, r.WorkDate), out var shift);
            if (r.ClockInTime is null && r.ClockOutTime is null) continue;

            if (r.ClockInTime is { } ci0 && r.ClockOutTime is { } co0 && co0 <= ci0)
                issues.Add(Make(AuditInverted, "warning", $"下班 {co0:MM-dd HH:mm} 不晚于上班 {ci0:MM-dd HH:mm}，工时无法按打卡核对"));

            if (r.AttendanceStatus == AttendanceStatus.Absent && r.ClockInTime is not null)
                issues.Add(Make(AuditAbsentWithPunch, "danger", $"有上班卡 {r.ClockInTime:MM-dd HH:mm}，状态却是旷工", r.ActualWorkHours));

            if (shift is null)
            {
                if (r.ClockInTime is not null && !(u.AttendanceGroup?.GroupName is { } ignoredGroup && NoShiftIgnoredGroups.Contains(ignoredGroup)))
                    issues.Add(Make(AuditNoShift, "warning", "这天有打卡但没有排班：迟到早退、午间窗口、加班重叠检查都没有班次可对照", r.ActualWorkHours));
                continue;
            }

            if (r.ClockInTime is { } ci)
            {
                var night = IsHourlyNightShift(shift);   // 晚上开始的跨天班次才算夜班（08:30–00:00 这种"跨天"白班不算）
                var mismatch = (night && ci.Hour is >= 7 and < 17) || (!night && ci.Hour >= 22);
                if (mismatch)
                    issues.Add(Make(AuditShiftMismatch, "danger", $"排的是「{shift.ShiftName}」，上班卡却是 {ci:HH:mm}：多半排错了班次，工时可能不准"));
            }

            var hourly = IsHourlyOn(hourlyStarts, u.AttendanceGroupId, r.WorkDate);
            var expected = ExpectedWorkHoursForAudit(r, shift, hourly, punchesByUser.GetValueOrDefault(r.UserId) ?? [], defaultDaily);
            if (expected is { } e && Math.Abs(e - r.ActualWorkHours) >= 0.01m)
                issues.Add(Make(AuditHours, "danger",
                    $"上班 {r.ClockInTime:HH:mm}、下班 {r.ClockOutTime:MM-dd HH:mm}、班次「{shift.ShiftName}」，按现行规则应为 {e:0.##} 小时，系统里是 {r.ActualWorkHours:0.##} 小时",
                    r.ActualWorkHours, e));
        }

        // 加班：已批准的加班单按班次对账之后的合计，和记录上的加班时长比（多了、少了都报）
        var users = records.Select(r => r.User).DistinctBy(x => x.Id).ToDictionary(x => x.Id);
        foreach (var row in await ReconcileOvertimeAsync(userIds, from, to, dryRun: true, ct, includeUnderRecorded: true))
        {
            users.TryGetValue(row.UserId, out var u);
            var under = row.OldOvertime < row.NewOvertime;
            if (Math.Abs(row.OldOvertime - row.NewOvertime) < 0.01m) continue;
            issues.Add(new AuditIssue(row.UserId, row.EmployeeNo, row.RealName, u?.AttendanceGroup?.GroupName, row.WorkDate,
                under ? AuditOtUnder : AuditOtOver, "danger",
                $"已批准的加班单（{row.Forms}）对账后合计 {row.NewOvertime:0.##} 小时，记录上是 {row.OldOvertime:0.##} 小时",
                row.OldOvertime, row.NewOvertime));
        }

        return issues
            .OrderBy(i => i.Severity == "danger" ? 0 : 1).ThenBy(i => i.Type).ThenBy(i => i.EmployeeNo).ThenBy(i => i.WorkDate)
            .ToList();
    }
}
