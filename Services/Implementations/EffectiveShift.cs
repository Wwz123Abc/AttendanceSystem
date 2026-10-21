using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// "这个人这一天按哪个班次算"：排了班就用排的班次；没排班（排班没跟上、延后补排）就退回所在考勤组"最常用的日班"，
/// 这样加班重叠检查、加班时长计算不会因为排班晚了几天就失效（2026-10-21 用户要求："无论是否排班、延后排班，
/// 系统都不应该把工时统计错误"。事故：朱志方等人 10/1–10/5 提交的 08:30 起的整天加班单，因为当时没排班，
/// "加班不能和班次重叠"的检查被整个跳过，正班 8 小时和整天加班重复计算）。
/// 兜底只在考勤组里有日班（非跨天班次）时才有；没有考勤组/没有日班的人没有兜底，行为和以前一样。
/// </summary>
public static class EffectiveShift
{
    /// <summary>考勤组最常用的日班：组内启用的非跨天班次里，被排班次数最多的那个（只有一个就是它）。</summary>
    public static async Task<ShiftSchedule?> GroupDefaultAsync(AttendanceDbContext db, int? groupId, CancellationToken ct = default,
        Dictionary<int, ShiftSchedule?>? cache = null)
    {
        if (groupId is not { } gid) return null;
        if (cache is not null && cache.TryGetValue(gid, out var cached)) return cached;   // 批量对账时同一个组只查一次
        var found = await GroupDefaultCoreAsync(db, gid, ct);
        if (cache is not null) cache[gid] = found;
        return found;
    }

    private static async Task<ShiftSchedule?> GroupDefaultCoreAsync(AttendanceDbContext db, int gid, CancellationToken ct)
    {
        var shifts = await db.ShiftSchedules.Where(s => s.AttendanceGroupId == gid && s.IsActive && !s.IsCrossDay).ToListAsync(ct);
        if (shifts.Count <= 1) return shifts.FirstOrDefault();
        var ids = shifts.Select(s => s.Id).ToList();
        var usage = await db.ShiftAssignments.Where(a => ids.Contains(a.ShiftScheduleId))
            .GroupBy(a => a.ShiftScheduleId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
        var best = usage.OrderByDescending(u => u.N).FirstOrDefault()?.Id;
        return shifts.FirstOrDefault(s => s.Id == best) ?? shifts[0];
    }

    /// <summary>这个人这一天实际排的班次；没排班返回 null（不兜底，要兜底用 <see cref="ForAsync"/>）。</summary>
    public static async Task<ShiftSchedule?> AssignedAsync(AttendanceDbContext db, int userId, DateOnly date, CancellationToken ct = default) =>
        await db.ShiftAssignments.Where(a => a.UserId == userId && a.WorkDate == date)
            .Select(a => a.ShiftSchedule).FirstOrDefaultAsync(ct);

    /// <summary>这个人离这一天最近的一次实际排班：先找这天之前最近的，没有再找这天之后最早的。夜班员工排班没跟上时，按他自己的夜班判断，不是按日班。</summary>
    public static async Task<ShiftSchedule?> NearestAssignedAsync(AttendanceDbContext db, int userId, DateOnly date, CancellationToken ct = default)
    {
        var before = await db.ShiftAssignments.Where(a => a.UserId == userId && a.WorkDate < date)
            .OrderByDescending(a => a.WorkDate).Select(a => a.ShiftSchedule).FirstOrDefaultAsync(ct);
        if (before is not null) return before;
        return await db.ShiftAssignments.Where(a => a.UserId == userId && a.WorkDate > date)
            .OrderBy(a => a.WorkDate).Select(a => a.ShiftSchedule).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 这个人这一天按哪个班次算：① 当天排的班；② 没排班 → 他最近一次实际排的班（夜班员工就按夜班）；③ 都没有 → 考勤组最常用的日班。
    /// IsFallback=true 表示不是当天实际排的班，"这天是不是休息日"要和正班那边一样只按周六周日判断（没排班的日子正班就是这样算的）。
    /// </summary>
    public static async Task<(ShiftSchedule? Shift, bool IsFallback)> ResolveAsync(AttendanceDbContext db, int userId, DateOnly date, int? groupId,
        CancellationToken ct = default, Dictionary<int, ShiftSchedule?>? cache = null)
    {
        if (await AssignedAsync(db, userId, date, ct) is { } assigned) return (assigned, false);
        if (await NearestAssignedAsync(db, userId, date, ct) is { } nearest) return (nearest, true);
        var def = await GroupDefaultAsync(db, groupId, ct, cache);
        return (def, def is not null);
    }

    /// <summary>排的班次，没排班就按 <see cref="ResolveAsync"/> 的顺序兜底。</summary>
    public static async Task<ShiftSchedule?> ForAsync(AttendanceDbContext db, int userId, DateOnly date, int? groupId, CancellationToken ct = default,
        Dictionary<int, ShiftSchedule?>? cache = null) => (await ResolveAsync(db, userId, date, groupId, ct, cache)).Shift;
}
