using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;

namespace AttendanceSystem.Services.Implementations;

public partial class AttendanceService
{
    /// <summary>重算历史工时最多往前追溯多少天（防止一次动太久远的、已经发过工资的数据）。</summary>
    private const int ShiftRecalcMaxDaysBack = 93;

    /// <summary>
    /// 班次改了午间窗口/上下班时间之后，把这个班次从某天起的历史考勤记录的工时按新设置重算一遍
    /// （改班次本身不会回头动历史记录，不重算的话旧记录的工时还是按旧窗口算的，对不上）。
    /// 只处理"排的是这个班次、上下班卡都有"的记录；只重算工时和午间命中，不动考勤状态、迟到早退、加班、请假；
    /// 出差/节假日记录的工时是审批流程定的，跳过；请假当天按请假封顶。口径和补卡后重算是同一个
    /// <see cref="ComputeDailyWorkHoursAsync"/>。返回（检查的记录数，工时有变化的记录数）。
    /// </summary>
    public async Task<(int Checked, int HoursChanged)> RecalcShiftHistoryAsync(int shiftId, DateOnly fromDate)
    {
        var today = DateOnly.FromDateTime(clock.LocalNow());
        if (fromDate < today.AddDays(-ShiftRecalcMaxDaysBack)) fromDate = today.AddDays(-ShiftRecalcMaxDaysBack);
        var shift = await db.ShiftSchedules.FirstOrDefaultAsync(s => s.Id == shiftId)
                    ?? throw new BusinessException("班次不存在");

        var records = await (from a in db.ShiftAssignments
                             join r in db.AttendanceRecords on new { a.UserId, a.WorkDate } equals new { r.UserId, r.WorkDate }
                             where a.ShiftScheduleId == shiftId && a.WorkDate >= fromDate && a.WorkDate <= today
                                   && r.ClockInTime != null && r.ClockOutTime != null
                             select r).Distinct().ToListAsync();
        var groupByUser = await db.Users.Where(u => records.Select(r => r.UserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.AttendanceGroupId);

        var checkedCount = 0;
        var changed = new List<AttendanceRecord>();
        foreach (var record in records)
        {
            if (record.AttendanceStatus is AttendanceStatus.Holiday or AttendanceStatus.BusinessTrip) continue;
            var ci = record.ClockInTime!.Value;
            var co = record.ClockOutTime!.Value;
            if (co <= ci) continue;
            checkedCount++;

            var oldHours = record.ActualWorkHours;
            var computed = await ComputeDailyWorkHoursAsync(record, record.WorkDate, ci, co, shift, groupByUser.GetValueOrDefault(record.UserId));
            record.ActualWorkHours = await CapWorkHoursForLeaveAsync(record, computed, shift, groupByUser.GetValueOrDefault(record.UserId));
            if (record.ActualWorkHours != oldHours)
            {
                AppendApprovalNote(record, $"班次设置已改，工时已按新设置重算（{oldHours:0.##}→{record.ActualWorkHours:0.##}）");
                record.UpdatedAt = clock.LocalNow();
                changed.Add(record);
            }
        }
        await db.SaveChangesAsync();

        // 工时变了的人，刷新对应月份的月度汇总（失败只记日志，数据已经保存）
        foreach (var grp in changed.GroupBy(r => (r.WorkDate.Year, r.WorkDate.Month)))
        {
            try { await GenerateMonthlySummaryAsync(grp.Key.Year, grp.Key.Month, grp.Select(r => r.UserId).Distinct().ToList()); }
            catch (Exception ex) { logger.LogError(ex, "班次历史工时已重算，但刷新 {Year}-{Month} 月度汇总失败", grp.Key.Year, grp.Key.Month); }
        }
        return (checkedCount, changed.Count);
    }
}
