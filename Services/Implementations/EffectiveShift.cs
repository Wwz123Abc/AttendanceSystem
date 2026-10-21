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
    public static async Task<ShiftSchedule?> GroupDefaultAsync(AttendanceDbContext db, int? groupId, CancellationToken ct = default)
    {
        if (groupId is not { } gid) return null;
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

    /// <summary>排的班次，没排班就用考勤组最常用的日班兜底。</summary>
    public static async Task<ShiftSchedule?> ForAsync(AttendanceDbContext db, int userId, DateOnly date, int? groupId, CancellationToken ct = default) =>
        await AssignedAsync(db, userId, date, ct) ?? await GroupDefaultAsync(db, groupId, ct);
}
