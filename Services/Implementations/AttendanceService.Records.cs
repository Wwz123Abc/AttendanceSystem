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

        return (await query.OrderByDescending(r => r.WorkDate).ToListAsync())
            .Select(ToDto).ToList();
    }

    /// <summary>查某部门/考勤组在一段时间内的所有人考勤记录。</summary>
    public async Task<List<AttendanceRecordDto>> GetDeptAttendanceAsync(DeptAttendanceQueryDto q, HashSet<int>? deptIds = null, CancellationToken ct = default)
    {
        var userIds = await BuildUserIdQueryAsync(q.DepartmentId, q.AttendanceGroupId, deptIds, excludeExempt: true);   // 先圈出这批人（免考勤的正式工不显示）

        return (await db.AttendanceRecords
            .Include(r => r.User).ThenInclude(u => u.Department)
            .Where(r => userIds.Contains(r.UserId)
                     && r.WorkDate >= q.StartDate
                     && r.WorkDate <= q.EndDate)
            .OrderBy(r => r.WorkDate).ThenBy(r => r.User.EmployeeNo)
            .ToListAsync(ct))
            .Select(ToDto).ToList();
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
