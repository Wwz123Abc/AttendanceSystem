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

/// <summary>用餐扣减的历史数据回填（<see cref="AttendanceService"/> 的一部分）。</summary>
public partial class AttendanceService
{
    public sealed record MealDeductionBackfillResult(
        int WorkHoursRecordsChanged, int OvertimeRequestsAdjusted, int OvertimeDaysChanged, int LeaveDaysChanged);

    /// <summary>
    /// 一次性回填：把 <paramref name="fromDate"/> 起、还按旧饭点规则（超过 6/9 小时扣整段）算好的
    /// 正班工时/加班时长/请假时长，按新规则（§7.16，固定 4 个时段按重叠扣减，2026-09-29 用户确认现在
    /// 就重算本周期）重新算一遍。不是常规流程的一部分，只手动跑这一次。<paramref name="dryRun"/>=true
    /// 时只计算、不写库（<c>db.SaveChangesAsync</c> 不会被调用，调用方也不应该复用这个 db 实例继续写别的），
    /// 用来先看一眼要改多少条、差值大不大，确认没问题再传 false 真正落库。
    /// 幂等：对已经是新规则算出来的记录重算结果不变，可以放心重复跑。
    /// </summary>
    public async Task<MealDeductionBackfillResult> RecalcMealDeductionBackfillAsync(DateOnly fromDate, bool dryRun)
    {
        var fromDateTime = fromDate.ToDateTime(TimeOnly.MinValue);
        var defaultDailyHours = appOptions.Value.DefaultDailyWorkHours;

        // ① 加班：按"天"重新汇总——同一天可能有多张已批准加班单，各自按新公式重新算一遍时长，
        // 求和后整体设成这天的 OvertimeHours（不能按差值累加：事后没法从一个累计数里拆出
        // "这张单当初贡献了多少"，只有按天整体重算再覆盖，才不会算错）。
        var overtimeApprovals = await db.ApprovalRequests
            .Where(a => a.ApprovalType == ApprovalType.Overtime && a.ApprovalStatus == ApprovalStatus.Approved
                     && a.OvertimeStartTime != null && a.OvertimeStartTime >= fromDateTime)
            .ToListAsync();
        var overtimeRequestsAdjusted = 0;
        foreach (var approval in overtimeApprovals)
        {
            if (approval.OvertimeEndTime is not { } end) continue;
            var newDuration = ComputeWorkHours(approval.OvertimeStartTime!.Value, end);
            if (approval.OvertimeDurationHours != newDuration)
            {
                approval.OvertimeDurationHours = newDuration;
                overtimeRequestsAdjusted++;
            }
        }
        var overtimeDaysChanged = 0;
        foreach (var group in overtimeApprovals.GroupBy(a => (a.ApplicantUserId, Day: DateOnly.FromDateTime(a.OvertimeStartTime!.Value))))
        {
            var record = await db.AttendanceRecords
                .FirstOrDefaultAsync(r => r.UserId == group.Key.ApplicantUserId && r.WorkDate == group.Key.Day);
            if (record is null) continue;
            var total = group.Sum(a => a.OvertimeDurationHours ?? 0);
            if (record.OvertimeHours == total) continue;
            AppendApprovalNote(record, $"按新饭点规则重算加班时长：{record.OvertimeHours:0.##}→{total:0.##} 小时");
            record.OvertimeHours = total;
            record.UpdatedAt = clock.LocalNow();
            overtimeDaysChanged++;
        }

        // ② 请假：同样按"天"重新汇总——查所有"结束时间落在回填窗口内或之后"的已批准假单（这样能捞到
        // 那些从窗口之前就开始、延续到窗口内的假单），每张单只重算 max(请假开始, fromDate) 到请假结束
        // 这一段（窗口之前的天数保持原值不动，不在这次回填范围内），逐日按新公式重新算交集时长再求和、
        // 整体设成这天的 LeaveHours（原因同上：多张假单叠加过的累计数没法事后拆分）。
        var leaveApprovals = await db.ApprovalRequests
            .Where(a => a.ApprovalType == ApprovalType.Leave && a.ApprovalStatus == ApprovalStatus.Approved
                     && a.LeaveStartTime != null && (a.LeaveEndTime ?? a.LeaveStartTime) >= fromDateTime)
            .ToListAsync();
        var dailyLeaveTotals = new Dictionary<(int UserId, DateOnly Day), decimal>();
        // 只有上班卡、没下班卡的半天假（下午请假常见场景）：审批回写当初是按"上班卡 → 这天请假
        // 开始的时间点"估算工时的（见 UpdateAttendanceAfterApprovalAsync 的请假分支），这里记下
        // 每人每天最早的那个请假分段起点，供下面第 ③ 步用同一套估算方式重新封顶
        // （2026-09-29 第 13 轮审查发现：以前只处理有上下班卡的记录，这类只有上班卡的半天假被漏了，
        // 第 ② 步已经把 LeaveHours 改成新值，工时却还停在按旧 LeaveHours 封顶的旧值，少算了工时）。
        var firstLeaveSegStartByDay = new Dictionary<(int UserId, DateOnly Day), DateTime>();
        foreach (var approval in leaveApprovals)
        {
            var lstart = approval.LeaveStartTime!.Value;
            var lend   = approval.LeaveEndTime ?? lstart;
            var sd = DateOnly.FromDateTime(lstart) > fromDate ? DateOnly.FromDateTime(lstart) : fromDate;
            var ed = DateOnly.FromDateTime(lend);
            if (sd > ed) continue;

            var leaveShiftsInRange = (await db.ShiftAssignments.Include(a => a.ShiftSchedule)
                    .Where(a => a.UserId == approval.ApplicantUserId && a.WorkDate >= sd && a.WorkDate <= ed)
                    .ToListAsync())
                .ToDictionary(a => a.WorkDate, a => a.ShiftSchedule);
            var skipNonWorkdays = !LeaveCountsNaturalDays(approval.LeaveType);

            for (var d = sd; d <= ed; d = d.AddDays(1))
            {
                leaveShiftsInRange.TryGetValue(d, out var leaveShift);
                if (!HasLeaveOverlapForDay(d, lstart, lend, leaveShift)) continue;
                if (skipNonWorkdays && IsShiftWeeklyRestDay(d, leaveShift)) continue;

                var dailyCap = leaveShift?.StandardWorkHours ?? defaultDailyHours;
                var hoursToday = ComputeLeaveHoursForDay(d, lstart, lend, dailyCap, leaveShift);
                var key = (approval.ApplicantUserId, d);
                dailyLeaveTotals[key] = dailyLeaveTotals.GetValueOrDefault(key) + hoursToday;

                // 同一天可能有多张假单叠加，取最早的那个分段起点（对"这天上午还工作了多久"是最保守、
                // 跟原逻辑最贴近的估算——原逻辑本来就是单张假单各自处理，这里只是没法回头拆分历史场景，
                // 取最早的起点）
                var segStart = lstart > d.ToDateTime(TimeOnly.MinValue) ? lstart : d.ToDateTime(TimeOnly.MinValue);
                if (!firstLeaveSegStartByDay.TryGetValue(key, out var existingStart) || segStart < existingStart)
                    firstLeaveSegStartByDay[key] = segStart;
            }
        }
        var leaveDaysChanged = 0;
        foreach (var ((uid, day), total) in dailyLeaveTotals)
        {
            var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == uid && r.WorkDate == day);
            if (record is null || record.LeaveHours == total) continue;
            AppendApprovalNote(record, $"按新饭点规则重算请假时长：{record.LeaveHours:0.##}→{total:0.##} 小时");
            record.LeaveHours = total;
            record.UpdatedAt = clock.LocalNow();
            leaveDaysChanged++;
        }

        // ③ 正班工时：有上下班卡的记录，复用手动补卡同一套重算逻辑——放在最后一步，这样上面①②
        // 改完之后的 OvertimeHours/LeaveHours 已经是最终值，这一步里 ApplyLeaveHoursCap 用到的
        // "已批准请假小时数"读到的就是回填后的正确数字（半天假 + 真实打卡这种场景需要这个顺序）。
        var punchRecords = await db.AttendanceRecords
            .Where(r => r.WorkDate >= fromDate && r.ClockInTime != null && r.ClockOutTime != null)
            .ToListAsync();
        var workHoursChanged = 0;
        foreach (var record in punchRecords)
        {
            var before = record.ActualWorkHours;
            await RecalcWorkHoursAfterManualPunchAsync(record, record.UserId);
            if (record.ActualWorkHours != before)
            {
                record.UpdatedAt = clock.LocalNow();   // 让"我的记录/我的日历"知道这天改过，打开时会触发月度汇总刷新
                workHoursChanged++;
            }
        }

        // ③b 只有上班卡、没下班卡的半天假：上面 punchRecords 这一批要求同时有上下班卡，会漏掉这种记录
        // （2026-09-29 第 13 轮审查发现）。跟 UpdateAttendanceAfterApprovalAsync 请假分支同一套估算方式，
        // 用回填后的 LeaveHours（第 ② 步已经改成新值）重新封顶。
        foreach (var ((uid, day), leaveSegStart) in firstLeaveSegStartByDay)
        {
            var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == uid && r.WorkDate == day);
            if (record is not { ClockInTime: { } ci, ClockOutTime: null } || record.AttendanceStatus != AttendanceStatus.OnLeave) continue;
            if (ci >= leaveSegStart) continue;
            var shift = (await GetShiftAssignmentAsync(uid, day))?.ShiftSchedule;
            var newHours = ApplyLeaveHoursCap(ComputeWorkHours(ci, leaveSegStart), record.LeaveHours,
                ResolveDailyStandardHours(shift, defaultDailyHours));
            if (record.ActualWorkHours == newHours) continue;
            record.ActualWorkHours = newHours;
            record.UpdatedAt = clock.LocalNow();
            workHoursChanged++;
        }

        if (!dryRun)
        {
            await db.SaveChangesAsync();
            // 回填涉及的每个月整月重算一次汇总——一次性操作，全员重算可以接受，不然"我的记录/我的日历"、
            // 月度报表这些读缓存汇总的地方要等到下月初后台任务才会自动纠正（2026-09-29 第 13 轮审查发现）。
            var today = DateOnly.FromDateTime(clock.LocalToday());
            for (var m = new DateOnly(fromDate.Year, fromDate.Month, 1); m <= today; m = m.AddMonths(1))
                await GenerateMonthlySummaryAsync(m.Year, m.Month);
        }
        return new MealDeductionBackfillResult(workHoursChanged, overtimeRequestsAdjusted, overtimeDaysChanged, leaveDaysChanged);
    }
}
