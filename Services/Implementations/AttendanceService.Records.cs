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

/// <summary>考勤记录查询：今天、个人、部门、月度汇总与排班（<see cref="AttendanceService"/> 的一部分）。</summary>
public partial class AttendanceService
{
    public async Task<AttendanceRecordDto?> GetTodayAttendanceAsync(int userId, DateTime? now = null)
    {
        var nowTime = now ?? clock.LocalNow();   // now 只给测试用：可以指定"现在是几点"
        var today  = DateOnly.FromDateTime(nowTime);
        var record = await db.AttendanceRecords
            .Include(r => r.User)
            .ThenInclude(u => u.Department)
            .FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == today);

        // 今天没有记录，或者只有一条"没有上班卡"的空记录（请假/出差/节假日审批会提前给未来的日子建记录）时，
        // 都要看昨天的夜班是不是还没下班：夜班员工在请假/出差日的早上下班，如果只因为"今天有一条记录"就不再看昨天，
        // 这张下班卡会被当成"今天的上班卡"（然后被"上班时间太早"拦下，或者记成错误的上班卡），昨天那班永远没有下班卡
        // （2026-09-28 全项目审查发现）
        if (record is null || record.ClockInTime is null)
        {
            var yesterday = today.AddDays(-1);
            var openYesterday = await db.AttendanceRecords
                .Include(r => r.User).ThenInclude(u => u.Department)
                .FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == yesterday
                                       && r.ClockInTime != null && r.ClockOutTime == null);
            if (openYesterday is not null)
            {
                var yesterdayAssignment = await GetShiftAssignmentAsync(userId, yesterday);
                if (yesterdayAssignment?.ShiftSchedule is { IsCrossDay: true } ys && IsWithinNightCarryOver(yesterday, ys, nowTime))
                    record = openYesterday;
                else if (IsPostMidnightClockOutOfDayShift(nowTime, openYesterday.ClockInTime!.Value, yesterdayAssignment?.ShiftSchedule,
                                                          (await GetShiftAssignmentAsync(userId, today))?.ShiftSchedule))
                    record = openYesterday;   // 白班加班过零点：这次该打的是昨天那条记录的下班卡
            }
        }

        return record is null ? null : ToDto(record);
    }

    /// <summary>查某员工的考勤记录列表（可按日期段或年月过滤）。</summary>
    public async Task<List<AttendanceRecordDto>> GetPersonalAttendanceAsync(PersonalAttendanceQueryDto q)
    {
        var query = db.AttendanceRecords
            .Include(r => r.User)
            .Where(r => r.UserId == q.UserId)
            .AsQueryable();

        if (q.StartDate.HasValue) query = query.Where(r => r.WorkDate >= q.StartDate.Value);
        if (q.EndDate.HasValue)   query = query.Where(r => r.WorkDate <= q.EndDate.Value);
        if (q.Year.HasValue && q.Month.HasValue)
            query = query.Where(r => r.WorkDate.Year == q.Year && r.WorkDate.Month == q.Month);

        var list = (await query.OrderByDescending(r => r.WorkDate).ToListAsync())
            .Select(ToDto).ToList();
        await MarkLateMidChecksAsync(list);
        return list;
    }

    /// <summary>
    /// 给考勤列表里"午间没在窗口内打上"的时段补上"迟到"标记：窗口结束之后、下班/下一段窗口之前有过午间打卡，
    /// 就显示成"迟到 hh:mm"，而不是"未打卡"。只改展示，不改工时口径（窗口没打上仍按午间漏打算工时）。
    /// 现算而不是存库，所以已有的历史记录也立刻能显示，不用重算数据。可以一次处理多个人的列表（按记录上的 UserId 分别查）。
    /// </summary>
    private async Task MarkLateMidChecksAsync(List<AttendanceRecordDto> dtos, CancellationToken ct = default)
    {
        var todo = dtos.Where(d => d.MidCheckHits.Any(h => !h.IsSatisfied)).ToList();
        if (todo.Count == 0) return;
        var userIds = todo.Select(d => d.UserId).Distinct().ToList();
        var minDate = todo.Min(d => d.WorkDate);
        var maxDate = todo.Max(d => d.WorkDate);
        var from = minDate.ToDateTime(TimeOnly.MinValue).AddDays(-1);
        var to   = maxDate.ToDateTime(TimeOnly.MinValue).AddDays(2);
        var punchRows = await db.AttendancePunches
            .Where(p => userIds.Contains(p.UserId) && p.PunchTime >= from && p.PunchTime <= to)
            .Select(p => new { p.UserId, p.PunchTime }).ToListAsync(ct);
        var punchesByUser = punchRows.GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.PunchTime).ToList());
        var shiftRows = await db.ShiftAssignments.Include(a => a.ShiftSchedule)
            .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= minDate && a.WorkDate <= maxDate)
            .ToListAsync(ct);
        var shifts = shiftRows.ToDictionary(a => (a.UserId, a.WorkDate), a => a.ShiftSchedule);
        foreach (var d in todo)
        {
            shifts.TryGetValue((d.UserId, d.WorkDate), out var shift);
            punchesByUser.TryGetValue(d.UserId, out var punches);
            d.MidCheckHits = FindLateMidChecks(d.WorkDate, shift, d.MidCheckHits, d.ClockOutTime, punches ?? []);
        }
    }

    /// <summary>
    /// 在没命中的窗口里找"迟到"的那次午间打卡：窗口结束之后的第一次打卡，并且要早于——下一段窗口的开始、
    /// 下班打卡、"应下班前 <see cref="ClockOutEligibleHoursBeforeEnd"/> 小时"（再晚就是下班那一次了）。
    /// 夜班窗口跨过午夜按班次推算到第二天。找不到就保持"未打卡"。
    /// </summary>
    public static List<MidCheckWindowResult> FindLateMidChecks(
        DateOnly workDate, ShiftSchedule? shift, List<MidCheckWindowResult> hits, DateTime? clockOut, List<DateTime> punches)
    {
        DateTime Resolve(TimeOnly t) => shift is null ? workDate.ToDateTime(t) : ResolveShiftTime(workDate, t, shift);
        DateTime? upperByShift = null;
        if (shift is not null)
        {
            var end = workDate.ToDateTime(shift.WorkEndTime);
            if (shift.IsCrossDay) end = end.AddDays(1);
            upperByShift = end.AddHours(-ClockOutEligibleHoursBeforeEnd);
        }
        var result = new List<MidCheckWindowResult>(hits.Count);
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            if (h.IsSatisfied) { result.Add(h); continue; }
            var windowEnd = Resolve(h.WindowEnd);
            var upper = DateTime.MaxValue;
            if (i + 1 < hits.Count) upper = Min(upper, Resolve(hits[i + 1].WindowStart));
            if (clockOut is { } co) upper = Min(upper, co);
            if (upperByShift is { } u) upper = Min(upper, u);
            var late = punches.Where(t => t > windowEnd && t < upper).OrderBy(t => t).Cast<DateTime?>().FirstOrDefault();
            result.Add(late.HasValue ? h with { LateTime = TimeOnly.FromDateTime(late.Value) } : h);
        }
        return result;

        static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    }

    /// <summary>查某部门/考勤组在一段时间内的所有人考勤记录。</summary>
    public async Task<List<AttendanceRecordDto>> GetDeptAttendanceAsync(DeptAttendanceQueryDto q, HashSet<int>? deptIds = null, CancellationToken ct = default)
    {
        var userIds = await BuildUserIdQueryAsync(q.DepartmentId, q.AttendanceGroupId, deptIds, excludeExempt: true);   // 先圈出这批人（免考勤的正式工不显示）

        var list = (await db.AttendanceRecords
            .Include(r => r.User).ThenInclude(u => u.Department)
            .Where(r => userIds.Contains(r.UserId)
                     && r.WorkDate >= q.StartDate
                     && r.WorkDate <= q.EndDate)
            .OrderBy(r => r.WorkDate).ThenBy(r => r.User.EmployeeNo)
            .ToListAsync(ct))
            .Select(ToDto).ToList();
        await MarkLateMidChecksAsync(list, ct);
        return list;
    }

    /// <summary>取某员工某月的汇总（含每日明细）。</summary>
    public async Task<MonthlySummaryDto?> GetMonthlySummaryAsync(int userId, int year, int month)
    {
        var summary = await db.MonthlyAttendanceSummaries
            .Include(s => s.User).ThenInclude(u => u.Department)
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Year == year && s.Month == month);
        if (summary is null) return null;

        // 这个月的第一天到最后一天
        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);
        var daily = await db.AttendanceRecords
            .Include(r => r.User)
            .Where(r => r.UserId == userId && r.WorkDate >= start && r.WorkDate <= end)
            .OrderBy(r => r.WorkDate).ToListAsync();

        var dto = MapSummary(summary, daily.Select(ToDto).ToList());
        var night = await ComputeNightShiftDaysAsync([userId], year, month);
        dto.NightShiftDays = night.GetValueOrDefault(userId);
        dto.NoShiftDays    = (await ComputeNoShiftDaysAsync([userId], start, end)).GetValueOrDefault(userId);
        return dto;
    }

    /// <summary>取某员工某月的排班安排（“我的排班”页用），按日期排序。</summary>
    public Task<List<MyScheduleDto>> GetMyScheduleAsync(int userId, int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        return GetMyScheduleAsync(userId, start, start.AddMonths(1).AddDays(-1));
    }

    public async Task<List<MyScheduleDto>> GetMyScheduleAsync(int userId, DateOnly start, DateOnly end)
    {
        return (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => a.UserId == userId && a.WorkDate >= start && a.WorkDate <= end)
                .OrderBy(a => a.WorkDate)
                .ToListAsync())
            .Select(a => new MyScheduleDto
            {
                WorkDate       = a.WorkDate,
                ShiftName      = a.ShiftSchedule.ShiftName,
                ShiftColor     = a.ShiftSchedule.Color,
                WorkStartText  = a.ShiftSchedule.WorkStartTime.ToString("HH:mm"),
                WorkEndText    = a.ShiftSchedule.WorkEndTime.ToString("HH:mm"),
                IsCrossDay     = a.ShiftSchedule.IsCrossDay,
                IsAutoAssigned = a.IsAutoAssigned
            })
            .ToList();
    }

    /// <summary>
    /// 批量算"有打卡但没排班"的天数（按员工）：没排班的人正班工时不按班次封顶（没有 StandardWorkHours 可封），
    /// 工作日晚上的加班时间已经算在正班里，加班单又记一遍，月度汇总里"正班工时 + 加班"会重复。
    /// 导出表用这个数给这类员工加标注，提醒算薪时不要直接相加。
    /// </summary>
    internal async Task<Dictionary<int, int>> ComputeNoShiftDaysAsync(List<int> userIds, DateOnly start, DateOnly end)
    {
        if (userIds.Count == 0) return [];
        var punched = await db.AttendanceRecords
            .Where(r => userIds.Contains(r.UserId) && r.WorkDate >= start && r.WorkDate <= end
                        && (r.ClockInTime != null || r.ClockOutTime != null))
            .Select(r => new { r.UserId, r.WorkDate }).ToListAsync();
        var assigned = (await db.ShiftAssignments
            .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= start && a.WorkDate <= end)
            .Select(a => new { a.UserId, a.WorkDate }).ToListAsync())
            .Select(a => (a.UserId, a.WorkDate)).ToHashSet();
        return punched.Where(p => !assigned.Contains((p.UserId, p.WorkDate)))
            .GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.Count());
    }
}
