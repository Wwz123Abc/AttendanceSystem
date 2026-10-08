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

/// <summary>报表：部门月度汇总、发薪考勤汇总表（模板汇总）、打卡时间明细（<see cref="AttendanceService"/> 的一部分）。</summary>
public partial class AttendanceService
{
    /// <summary>
    /// 取某部门/考勤组某月的汇总列表（不含每日明细）。
    /// 这里故意不按"当前是否在职"过滤——月度报表是历史记录，员工哪怕后来离职/停用了，
    /// 只要那个月确实生成过汇总，也应该继续能查到，不然离职员工那个月的数据会从报表里凭空消失。
    /// 部门/考勤组这两个字段员工离职后仍然保留（停用不会清空），所以按它们筛选不受影响。
    /// </summary>
    public async Task<List<MonthlySummaryDto>> GetDeptMonthlySummariesAsync(
        int? deptId, int? groupId, int year, int month, HashSet<int>? scopeDeptIds = null, CancellationToken ct = default)
    {
        var idQuery = db.Users.NeedingAttendance();   // 免考勤的正式工不进汇总表
        if (deptId.HasValue)  idQuery = idQuery.Where(u => u.DepartmentId == deptId.Value);
        if (groupId.HasValue) idQuery = idQuery.Where(u => u.AttendanceGroupId == groupId.Value);
        if (scopeDeptIds is not null) idQuery = idQuery.Where(u => u.DepartmentId != null && scopeDeptIds.Contains(u.DepartmentId.Value));
        var userIds = await idQuery.Select(u => u.Id).ToListAsync(ct);

        var dtos = (await db.MonthlyAttendanceSummaries
            .Include(s => s.User).ThenInclude(u => u.Department)
            .Where(s => userIds.Contains(s.UserId) && s.Year == year && s.Month == month)
            .OrderBy(s => s.User.Department!.DeptName).ThenBy(s => s.User.EmployeeNo)
            .ToListAsync(ct))
            .Select(s => MapSummary(s, [])).ToList();

        // 夜班天数不单独存表，这里按排班/打卡时间批量算出来回填
        var night = await ComputeNightShiftDaysAsync(dtos.Select(d => d.UserId).ToList(), year, month);
        foreach (var d in dtos) d.NightShiftDays = night.GetValueOrDefault(d.UserId);
        var mStart = new DateOnly(year, month, 1);
        var noShift = await ComputeNoShiftDaysAsync(dtos.Select(d => d.UserId).ToList(), mStart, mStart.AddMonths(1).AddDays(-1));
        foreach (var d in dtos) d.NoShiftDays = noShift.GetValueOrDefault(d.UserId);
        return dtos;
    }

    /// <summary>
    /// 生成"模板月度汇总表"：统计周期是调用方传入的任意起止日期（比如"上月26号至本月25号"这种薪资结算周期），
    /// 不依赖 MonthlyAttendanceSummary 这张按自然月生成的汇总表，而是直接从每日考勤记录/排班/假期现算，
    /// 这样才能支持不是自然月的统计区间。可选按部门 / 按考勤组自动带出的所属公司筛选。
    /// </summary>
    public async Task<TemplateReportResultDto> GenerateTemplateReportAsync(DateOnly start, DateOnly end, List<int>? deptIds, CancellationToken ct = default)
        => await GenerateTemplateReportCoreAsync(start, end, deptIds, null, ct);

    public async Task<TemplateReportRowDto?> GetMyPayrollRowAsync(int userId, DateOnly start, DateOnly end, CancellationToken ct = default)
        => (await GenerateTemplateReportCoreAsync(start, end, null, userId, ct)).Rows.FirstOrDefault();

    private async Task<TemplateReportResultDto> GenerateTemplateReportCoreAsync(
        DateOnly start, DateOnly end, List<int>? deptIds, int? onlyUserId, CancellationToken ct)
    {
        var dates = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1)) dates.Add(d);
        var defaultDailyHours = appOptions.Value.DefaultDailyWorkHours;

        // 范围 = 现在在职的人 ∪ 这段周期里有考勤记录的人——这份表是给发工资用的，
        // 如果只按"现在是否在职"筛选，员工在这个薪资周期里离职、导出报表时已经被停用，
        // 就会整个人从表里消失，那个月的工资就算不出来了，所以必须把"曾经有过记录的人"也纳进来。
        var relevantIds = await db.Users.Where(u => u.IsActive).Select(u => u.Id)
            .Union(db.AttendanceRecords.Where(r => r.WorkDate >= start && r.WorkDate <= end).Select(r => r.UserId))
            .Distinct()
            .ToListAsync(ct);

        var q = db.Users
            .Include(u => u.Department)
            .Include(u => u.AttendanceGroup)
            .Where(u => relevantIds.Contains(u.Id))
            .NeedingAttendance();   // 免考勤的正式工（管理员/文员/主管/班组长，或勾了免考勤）不进报表
        // deptIds 是页面那棵"公司/部门"合并树里勾选出来的部门编号（勾大范围=公司节点，会连带展开成它底下所有部门的编号）；
        // 不勾任何部门 = 不筛选，导出全公司所有人。
        if (deptIds is { Count: > 0 }) q = q.Where(u => u.DepartmentId.HasValue && deptIds.Contains(u.DepartmentId.Value));
        if (onlyUserId.HasValue) q = q.Where(u => u.Id == onlyUserId.Value);   // 员工看自己那一行（跟导出表同一套算法）
        var users = await q.OrderBy(u => u.Department!.DeptName).ThenBy(u => u.EmployeeNo).ToListAsync(ct);
        var userIds = users.Select(u => u.Id).ToList();

        // 批量预取这段时间的考勤记录/排班/假期，循环里直接从内存取，避免每人都查一次库
        var recordsByUser = (await db.AttendanceRecords
                .Where(r => userIds.Contains(r.UserId) && r.WorkDate >= start && r.WorkDate <= end)
                .ToListAsync(ct))
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.WorkDate));

        var assignByUser = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= start && a.WorkDate <= end)
                .ToListAsync(ct))
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.WorkDate));

        var night = await ComputeNightShiftDaysRangeAsync(userIds, start, end);

        var rows = new List<TemplateReportRowDto>();
        foreach (var user in users)
        {
            var recByDate    = recordsByUser.GetValueOrDefault(user.Id) ?? new Dictionary<DateOnly, AttendanceRecord>();
            var assignByDate = assignByUser.GetValueOrDefault(user.Id) ?? new Dictionary<DateOnly, ShiftAssignment>();

            var row = new TemplateReportRowDto
            {
                UserId          = user.Id,
                RealName        = user.RealName,
                GroupName       = user.AttendanceGroup?.GroupName,
                DeptName        = user.Department?.DeptName,
                EmployeeNo      = user.EmployeeNo,
                Position        = user.Position,
                ContractCompany = user.ContractCompany,
                NightShiftDays  = night.GetValueOrDefault(user.Id)
            };

            // 标准工时：取这段时间里出现次数最多的那个班次的标准工时（没排过班就留空）
            row.StandardDailyHours = assignByDate.Values
                .GroupBy(a => a.ShiftScheduleId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.First().ShiftSchedule.StandardWorkHours)
                .FirstOrDefault();
            if (assignByDate.Count == 0) row.StandardDailyHours = null;

            decimal totalWork = 0, businessTripHours = 0, nightShiftHours = 0;
            decimal totalOtHours = 0, weekdayOtHours = 0, restOtHours = 0;
            decimal actualDays = 0, leaveDays = 0;
            int absentDays = 0, missingIn = 0, missingOut = 0, noShiftDays = 0;
            int lateMin = 0, earlyMin = 0, lateCnt = 0, earlyCnt = 0;

            foreach (var date in dates)
            {
                // 原来这里有一条"入职日之前的日子不算这个人的考勤范围"的跳过逻辑，是为了防止
                // "当天没有考勤记录、又不是休息日"被误判成旷工；但 2026-09-22 统一旷工口径之后，
                // 没有记录的日子本来就不会再被算成旷工了（只认后台任务真正标记的 Absent 记录），
                // 这条跳过逻辑的原始目的已经不存在。而它还有一个没被注意到的副作用：如果入职日期
                // 是后补录/改晚的，员工入职日之前如果真的有考勤记录（打卡本身是真实发生的），
                // 这条记录会被整天跳过、不计入出勤天数/工时/缺卡统计——跟 GenerateMonthlySummaryAsync
                // （只用 HireDate 调整"应出勤天数"，从不过滤已有记录）不一致，数据核查时发现过实际案例。
                // 现在改成跟月度汇总一样：有记录就正常处理，HireDate 不再在这里过滤任何一天。
                recByDate.TryGetValue(date, out var rec);
                assignByDate.TryGetValue(date, out var assign);
                var isShiftRest  = IsShiftWeeklyRestDay(date, assign?.ShiftSchedule);  // 排的班自己配置的每周休息日，或没排班时按周末兜底

                // 当天是不是上的夜班：排班里配的是跨天班次/名字带"夜"就算；没排班时按打卡时间兜底
                // （18 点后上班，或下班跨到了第二天），口径和 ComputeNightShiftDaysRangeAsync 保持一致。
                var isNightShift = assign is not null && (assign.ShiftSchedule.IsCrossDay || assign.ShiftSchedule.ShiftName.Contains('夜'));
                if (!isNightShift && assign is null && rec?.ClockInTime is { } nci)   // 兜底只对"没排班"的日子：排了白班/中班的人休息日晚上来打卡、加班过零点，不算夜班
                {
                    if (nci.Hour >= 18) isNightShift = true;
                    else if (rec.ClockOutTime is { } nco && nco.Date > nci.Date) isNightShift = true;
                }
                row.DailyIsNightShift.Add(isNightShift);
                row.DailyIsRest.Add(isShiftRest);

                decimal? dayHours = null;
                if (rec is not null)
                {
                    var dailyStdHours = assign?.ShiftSchedule.StandardWorkHours ?? defaultDailyHours;
                    // 发薪口径（2026-10-08，用户确认"每天工时=正班+加班，直接按总工时发工资"）：
                    // 正班——休息日一律 0；工作日最多算班次标准工时（没排班按默认 8 小时）。超出的部分是晚上的加班，
                    // 只认加班单，不然没排班的人（正班不封顶、已含工作日加班）"正班+加班"会把同一段在岗时间算两遍；
                    // 同时也把 9/28 前"休息日既记正班又记加班"的旧记录挡在报表外。
                    var regularHalf = isShiftRest ? 0m : FloorToHalf(Math.Min(rec.ActualWorkHours, dailyStdHours));
                    var otHalf      = rec.OvertimeHours > 0 ? FloorToHalf(rec.OvertimeHours) : 0m;
                    if (regularHalf + otHalf > 0) dayHours = regularHalf + otHalf;   // 每日格子 = 正班 + 加班（半小时取整，不足舍去）
                    totalWork += regularHalf;                                          // 合计按"半小时"为最小单位累加，保证总数只会是整数或 x.5
                    if (isNightShift) nightShiftHours += regularHalf;                  // 夜班总工时：当天算夜班才计入
                    // 出勤天数/请假天数跟 GenerateMonthlySummaryAsync 共用同一个公式（ResolveAttendanceDayCredit），
                    // 不能只看"有没有工时"——半天假当天可能 ActualWorkHours>0（上午上班），整天假是 0，
                    // 两种都要正确记到"出勤"和"请假"里，不能像以前那样只用 ActualWorkHours>0 判断出勤、
                    // 完全没有"请假天数"这个概念，导致这份发工资用的报表和月度汇总页对不上
                    // （发现于 2026-09-18 数据核查）。
                    actualDays += ResolveAttendanceDayCredit(rec, dailyStdHours, isShiftRest);
                    if (rec.AttendanceStatus == AttendanceStatus.OnLeave)
                        leaveDays += ResolveLeaveDaysFraction(rec.LeaveHours, dailyStdHours);
                    if (rec.AttendanceStatus == AttendanceStatus.BusinessTrip) businessTripHours += FloorToHalf(rec.ActualWorkHours);
                    if (assign is null && (rec.ClockInTime is not null || rec.ClockOutTime is not null)) noShiftDays++;   // 有打卡但没排班
                    if (rec.AttendanceStatus == AttendanceStatus.Absent && !user.IsExemptFromAttendance()) absentDays++;   // 免考勤的人不统计旷工
                    // 缺卡/迟到/早退次数的判定口径统一改成跟 GenerateMonthlySummaryAsync 一样按"状态"算
                    // （不再按分钟数/裸打卡时间判断），并排除旷工/请假/节假日/出差——不然半天假当天上午
                    // 迟到、或旷工那天"当然两次都没打"，会被这份表额外多算一次迟到/缺卡，跟月度汇总的
                    // 结论对不上（2026-09-22 统一口径）。
                    var excludedFromMissing = rec.AttendanceStatus is AttendanceStatus.Absent or AttendanceStatus.OnLeave
                        or AttendanceStatus.Holiday or AttendanceStatus.BusinessTrip;
                    if (!excludedFromMissing && rec.ClockInTime is null && rec.ClockOutTime is not null) missingIn++;    // 有下班卡没上班卡
                    if (!excludedFromMissing && rec.ClockInTime is not null && rec.ClockOutTime is null) missingOut++;   // 有上班卡没下班卡
                    // 迟到/早退"分钟"和"次数"必须同一个口径：都只认"状态就是迟到/早退"的记录。以前次数按状态数、
                    // 分钟却把所有记录的 LateMinutes 直接相加，结果"未打卡"记录（当天只有一次很晚的打卡，先被当成
                    // 上班卡算出几百分钟迟到，后来又被后台改成"未打卡"，分钟数却没清）、半天假记录上残留的分钟
                    // 也被加了进来——一个月的迟到合计里有近一半是这样虚出来的，还出现"迟到 1600 分钟、迟到 1 次"
                    // 这种分钟和次数对不上的行（2026-09-24 数据核查）。
                    lateMin  += EffectiveLateMinutes(rec);
                    earlyMin += EffectiveEarlyLeaveMinutes(rec);
                    if (rec.AttendanceStatus == AttendanceStatus.Late) lateCnt++;
                    if (rec.AttendanceStatus == AttendanceStatus.EarlyLeave) earlyCnt++;

                    if (rec.OvertimeHours > 0)
                    {
                        // 参考模板里"加班总时长"这几列的单位是小时，跟"工作时长"同一个口径，不用再换算。
                        // 每天的加班先按半小时取整再累加（而不是最后对合计取整）：这样"工作日+休息日"
                        // 两个分项加起来一定正好等于"加班总时长"，不会因为各自取整出现对不上的尾差。
                        var ot = FloorToHalf(rec.OvertimeHours);
                        totalOtHours += ot;
                        if (isShiftRest) restOtHours += ot;
                        else weekdayOtHours += ot;
                    }
                }
                // 注意：这里不再对"完全没有考勤记录"的日子额外判定旷工——跟 GenerateMonthlySummaryAsync
                // 统一口径，只认后台任务（AttendanceBackgroundService.MarkAbsentAsync）真正生成的
                // AttendanceStatus.Absent 记录。旧逻辑会把"没填入职日期""中途停用后"这类日子也误判成
                // 旷工；MarkAbsentAsync 本身已经正确跳过了未入职（HireDate 为空/未到）和非在职用户，
                // 两份报表统一改成只信它生成的结果，不用各自再猜一遍"这天算不算旷工"（2026-09-22 统一口径）。

                row.DailyHours.Add(dayHours);
            }

            // 应出勤天数：跟 GenerateMonthlySummaryAsync 同一个口径（同一个 CountExpectedWorkdays），
            // 入职日期晚于周期开始的从入职日起算；没填入职日期的按整个周期算
            var effStart = user.HireDate is { } hireDate && hireDate > start ? hireDate : start;
            // 免考勤的人不需要打卡，没有"应出勤"这回事（不然会显示"应出勤 22 天 / 出勤 0 天"，像是整月没来）
            row.ExpectedWorkdays = user.IsExemptFromAttendance() || effStart > end ? 0
                : CountExpectedWorkdays(effStart, end, assignByDate.ToDictionary(a => a.Key, a => a.Value.ShiftSchedule));

            // 各项工时在上面累加时就已经按"半小时"取整过了，这里直接赋值（合计只会是整数或 x.5）
            row.ActualWorkdays          = actualDays;
            row.LeaveDays               = leaveDays;
            // 正班工时：不含加班。以前这里还有一个"总工时=正班+加班"的字段，跟 GenerateMonthlySummaryAsync/
            // "我的记录"统一口径后变成跟正班工时数值完全相同，2026-09-22 直接删掉了那个重复字段和对应的列
            // （加班单独看 TotalOvertimeHours），不用两处都留着同一个数字。
            row.RegularWorkHours        = totalWork;
            row.PayableHours            = totalWork + totalOtHours;   // 实际总工时 = 正班 + 加班
            row.NoShiftDays             = noShiftDays;
            row.LateMinutes             = lateMin;
            row.EarlyLeaveMinutes       = earlyMin;
            row.LateCount               = lateCnt;
            row.EarlyLeaveCount         = earlyCnt;
            row.MissingClockInCount     = missingIn;
            row.MissingClockOutCount    = missingOut;
            row.AbsentDays              = absentDays;
            row.BusinessTripHours       = businessTripHours;
            row.NightShiftHours         = nightShiftHours;
            row.TotalOvertimeHours      = totalOtHours;
            row.WeekdayOvertimeHours    = weekdayOtHours;
            row.RestDayOvertimeHours    = restOtHours;

            rows.Add(row);
        }

        return new TemplateReportResultDto { StartDate = start, EndDate = end, Dates = dates, Rows = rows };
    }

    /// <summary>取"打卡时间表"导出用的数据，人员范围口径和 <see cref="GenerateTemplateReportAsync"/> 保持一致。</summary>
    public async Task<List<AttendanceRecordDto>> GetClockTimeSheetAsync(DateOnly start, DateOnly end, List<int>? deptIds, CancellationToken ct = default)
    {
        var relevantIds = await db.Users.Where(u => u.IsActive).Select(u => u.Id)
            .Union(db.AttendanceRecords.Where(r => r.WorkDate >= start && r.WorkDate <= end).Select(r => r.UserId))
            .Distinct()
            .ToListAsync(ct);

        var uq = db.Users.Where(u => relevantIds.Contains(u.Id)).NeedingAttendance();
        if (deptIds is { Count: > 0 }) uq = uq.Where(u => u.DepartmentId.HasValue && deptIds.Contains(u.DepartmentId.Value));
        var userIds = await uq.Select(u => u.Id).ToListAsync(ct);

        return (await db.AttendanceRecords
                .Include(r => r.User).ThenInclude(u => u.Department)
                .Where(r => userIds.Contains(r.UserId) && r.WorkDate >= start && r.WorkDate <= end)
                .OrderBy(r => r.User.EmployeeNo).ThenBy(r => r.WorkDate)
                .ToListAsync(ct))
            .Select(ToDto).ToList();
    }
}
