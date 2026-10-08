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

/// <summary>月度汇总的生成与刷新（<see cref="AttendanceService"/> 的一部分）。</summary>
public partial class AttendanceService
{
    /// <summary>
    /// 生成/重算某月的考勤汇总（已存在就更新，没有就新建）。
    /// 处理范围不能只看"现在是否在职"：如果一个人这个月工作过、后来才离职（停用），
    /// 那他这个月的汇总必须照常算出来/保持更新，不能因为人已经离职就让这个月的历史记录消失
    /// ——不然离职员工最后一个月的考勤/工时在报表里会直接对不上账，这是财务和 HR 都要用的数据。
    /// 所以处理范围 = 现在在职的人 ∪ 这个月有考勤记录的人 ∪ 这个月已经生成过汇总的人。
    /// <paramref name="onlyUserId"/> 不为空时只重算这一个人（补卡/请假/加班/出差审批回写、
    /// 管理员手动补卡之后调用这个，避免月初已经生成过的汇总因为后补的记录而跟日明细对不上、
    /// 又得靠人工去点"重新生成"才能刷新）。
    /// </summary>
    public async Task GenerateMonthlySummaryAsync(int year, int month, IReadOnlyCollection<int>? onlyUserIds = null)
    {
        try
        {
            await GenerateMonthlySummaryCoreAsync(year, month, onlyUserIds);
        }
        catch (DbUpdateException)
        {
            // (UserId, Year, Month) 有唯一索引：月初后台任务和管理员手动重算几乎同时跑时，两边都认为"这个人这个月
            // 还没有汇总"而各插一条，后到的那次 SaveChanges 整批回滚、当月汇总静默缺失。清掉没存成功的改动重来
            // 一次——这时对方那条已经落库了，会走"已有汇总→更新"的分支（项目里其它有唯一索引的写入路径也是这么重试的）
            db.ChangeTracker.Clear();
            await GenerateMonthlySummaryCoreAsync(year, month, onlyUserIds);
        }
    }

    private async Task GenerateMonthlySummaryCoreAsync(int year, int month, IReadOnlyCollection<int>? onlyUserIds)
    {
        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);

        // 传了名单（哪怕是空名单）就只处理名单里的人；只有完全没传（null）才是"全公司重算"。
        // 以前判的是 Count > 0，空名单会落到全库分支——"没设范围的非 Admin 账号"或"范围内没有员工的
        // 分公司管理员"调用手动重算接口时，控制器传进来的是空名单，结果反而触发了全公司重算（2026-09-24 审查修复）。
        var candidateIds = onlyUserIds is not null
            ? onlyUserIds.Distinct().ToList()
            : await db.Users.Where(u => u.IsActive).Select(u => u.Id)
                .Union(db.AttendanceRecords.Where(r => r.WorkDate >= start && r.WorkDate <= end).Select(r => r.UserId))
                .Union(db.MonthlyAttendanceSummaries.Where(s => s.Year == year && s.Month == month).Select(s => s.UserId))
                .Distinct()
                .ToListAsync();
        var users = await db.Users.Where(u => candidateIds.Contains(u.Id)).NeedingAttendance().ToListAsync();   // 免考勤的正式工不生成汇总

        // 本月每个人“审批通过”的申请数（一次性批量查，避免循环里逐人查库）
        var startDt = start.ToDateTime(TimeOnly.MinValue);
        var endDt   = end.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var approvedByUser = (await db.ApprovalRequests
                .Where(a => a.ApprovalStatus == ApprovalStatus.Approved
                         && a.SubmittedAt >= startDt && a.SubmittedAt < endDt)
                .GroupBy(a => a.ApplicantUserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.UserId, x => x.Count);

        // 下面几张表按"这批人 + 这个月"一次性整批查出来，循环里直接从内存字典取——原来是每个人
        // 各自查一遍数据库（考勤记录、排班、所在考勤组），几百号人一次全量重算就是几百组重复查询，
        // 数据量小的假期表更是每人都重复查一遍同一个月的数据，现在全部改成先批量查、后内存过滤。
        var recordsByUser = (await db.AttendanceRecords
                .Where(r => candidateIds.Contains(r.UserId) && r.WorkDate >= start && r.WorkDate <= end)
                .ToListAsync())
            .GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.ToList());

        var shiftsByUser = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => candidateIds.Contains(a.UserId) && a.WorkDate >= start && a.WorkDate <= end)
                .ToListAsync())
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.WorkDate, a => a.ShiftSchedule));

        var existingSummaries = await db.MonthlyAttendanceSummaries
            .Where(s => candidateIds.Contains(s.UserId) && s.Year == year && s.Month == month)
            .ToDictionaryAsync(s => s.UserId);

        var defaultDailyHours = appOptions.Value.DefaultDailyWorkHours;

        foreach (var user in users)
        {
            var records     = recordsByUser.GetValueOrDefault(user.Id, []);
            var shiftByDate = shiftsByUser.GetValueOrDefault(user.Id) ?? new Dictionary<DateOnly, ShiftSchedule>();

            // 应出勤天数：从“月初”和“该员工入职日”里取较晚的一天开始算，
            // 避免月中入职的人被算成全月应出勤、导致出勤率虚低。
            var effStart = user.HireDate is { } hd && hd > start ? hd : start;
            var expected = user.IsExemptFromAttendance() || effStart > end ? 0 : CountExpectedWorkdays(effStart, end, shiftByDate);

            // 取出已有的汇总，没有就新建
            if (!existingSummaries.TryGetValue(user.Id, out var summary))
            {
                summary = new MonthlyAttendanceSummary { UserId = user.Id, Year = year, Month = month };
                db.MonthlyAttendanceSummaries.Add(summary);
                existingSummaries[user.Id] = summary;
            }

            // 工时/加班：正常情况下每条记录的 ActualWorkHours 在写入时（本地打卡/钉钉同步/补卡审批）就已经算好了，
            // 这里直接求和即可。仅对「历史遗留、写入时还没补算过」的记录（ActualWorkHours 仍是 0 但有上下班时间）
            // 现场补算，并且顺手写回记录本身——这样老数据只要被打开一次月度报表就能自愈，
            // 不会出现「日明细显示 0、月合计却不是 0」这种对不上的情况。
            decimal totalWork = 0, totalOt = 0;
            foreach (var r in records)
            {
                // 出差/节假日当天工时按审批口径本来就已经定好（标准工时/0），不能因为当天恰好也有
                // 打卡时间就被这里的"老数据补算"顺手覆盖掉。请假不再排除在外：这里本来就是"老数据
                // 按当前规则重新算一遍"的自愈机制，半天假当天如果有真实打卡，按标准工时封顶补算，
                // 全天假的记录重算结果仍然是 0（封顶为 0），不会产生变化——顺带把 09-17 之前支持
                // 半天假之前被整天清零的历史半天假记录，在下次打开月度报表时自动纠正回来。
                if (r.ActualWorkHours <= 0 && r.ClockInTime is { } ci && r.ClockOutTime is { } co && co > ci
                    && r.AttendanceStatus is not (AttendanceStatus.BusinessTrip or AttendanceStatus.Holiday))
                {
                    // 老数据补算工时口径要和写入时一致：早到晚走都不多算钱，加班只认审批（这里不猜、不动 OvertimeHours）。
                    // 缺打卡的窗口直接从记录本身已经存好的 MidCheckResults 里读，不用班次现在的配置反查
                    // （班次配置可能后来改过，用记录当时冻结下来的这份才准确）。
                    shiftByDate.TryGetValue(r.WorkDate, out var shift);
                    // 休息日不计正班工时（有没有批准的加班都一样：有加班的话只算加班）——跟本地打卡同一套规则
                    if (IsNonCompRestDay(r.WorkDate, shift))
                    {
                        r.ActualWorkHours = 0;
                    }
                    else
                    {
                        var midCheckResults = shift is not null ? r.MidCheckResults.ParseMidCheckResults() : [];
                        var missedEnds = shift is not null
                            ? ResolveMissedNonLastWindowEnds(r.WorkDate, shift, midCheckResults)
                            : [];
                        var effCi = ClampEffectiveClockIn(r.WorkDate, ci, shift, missedEnds);
                        var effCo = ClampEffectiveClockOut(r.WorkDate, co, shift, ResolveSecondHalfAbsentBoundary(r.WorkDate, shift, midCheckResults));
                        var computedHours = ComputeWorkHours(effCi, effCo);
                        r.ActualWorkHours = r.AttendanceStatus == AttendanceStatus.OnLeave
                            ? ApplyLeaveHoursCap(computedHours, r.LeaveHours, ResolveDailyStandardHours(shift, appOptions.Value.DefaultDailyWorkHours))
                            : computedHours;
                    }
                }
                // 每天的工时/加班按"半小时"为最小单位取整后再累加（不足半小时舍去），
                // 保证月度报表上的合计只会是整数或 x.5，不会出现 113.38 这种零碎小数
                totalWork += FloorToHalf(r.ActualWorkHours);
                totalOt   += FloorToHalf(r.OvertimeHours);
            }

            // 根据每日记录算出各项统计
            summary.ExpectedWorkdays  = expected;
            // 实际出勤 = 打了上班卡的天数 + 已批准出差的天数（旷工/未打卡都不算出勤）；
            // 请假当天如果也有真实打卡（半天假），只算"1 − 请假占比"那一部分出勤，不再跟 LeaveDays
            // 重复记满整天——不然半天假的人会变成"出勤 1 天 + 请假 0.5 天"，一天算出 1.5 天（发现于
            // 2026-09-18 发工资前的数据核查）。跟 GenerateTemplateReportAsync 共用 ResolveAttendanceDayCredit。
            summary.ActualWorkdays = records.Sum(r =>
                ResolveAttendanceDayCredit(r, ResolveDailyStandardHours(shiftByDate.GetValueOrDefault(r.WorkDate), defaultDailyHours),
                    IsShiftWeeklyRestDay(r.WorkDate, shiftByDate.GetValueOrDefault(r.WorkDate))));
            // 迟到/早退按「状态」统计（钉钉同步只写状态、不写分钟数，按分钟数会漏算）
            summary.LateCount         = records.Count(r => r.AttendanceStatus == AttendanceStatus.Late);
            summary.EarlyLeaveCount   = records.Count(r => r.AttendanceStatus == AttendanceStatus.EarlyLeave);
            summary.AbsentDays        = user.IsExemptFromAttendance() ? 0 : records.Count(r => r.AttendanceStatus == AttendanceStatus.Absent);   // 免考勤的人不统计旷工
            // 缺卡：状态=未打卡，或“只打了上/下班其中一次”（这样钉钉数据的缺卡也能识别；出差本就不用打卡，排除）
            summary.NotPunchedCount   = records.Count(r => r.AttendanceStatus == AttendanceStatus.NotPunched
                || ((r.ClockInTime.HasValue ^ r.ClockOutTime.HasValue)
                    && r.AttendanceStatus is not AttendanceStatus.Absent and not AttendanceStatus.OnLeave
                                          and not AttendanceStatus.Holiday and not AttendanceStatus.BusinessTrip));
            // 请假天数：按小时折算，不再是"这天状态是请假就算一整天"——半天假只占 0.5 天
            // （2026-09-17 支持半天请假）。
            summary.LeaveDays = records.Where(r => r.AttendanceStatus == AttendanceStatus.OnLeave)
                .Sum(r => ResolveLeaveDaysFraction(r.LeaveHours, ResolveDailyStandardHours(shiftByDate.GetValueOrDefault(r.WorkDate), defaultDailyHours)));
            summary.TotalOvertimeHours = totalOt;
            summary.TotalWorkHours    = totalWork;
            summary.ApprovedCount     = approvedByUser.GetValueOrDefault(user.Id);   // 本月审批通过次数（原来漏算，恒为 0）
            summary.UpdatedAt         = clock.LocalNow();
        }

        await db.SaveChangesAsync();
    }

    /// <summary>"我的记录"/"我的日历"共用：确保这个人这个月的汇总是新鲜的，不是每次打开页面都无条件
    /// 重算一遍——"我的日历"以前是每次 GET 都调 GenerateMonthlySummaryAsync，哪怕数据毫无变化也会
    /// 产生一次 UPDATE；"我的记录"则完全不重算，只读现有汇总行，当月还没生成过汇总时会显示空白，
    /// 跟"我的日历"（强制重算，总有数据）表现不一致，容易让人以为哪个页面出了 bug（2026-09-21
    /// 代码审查发现）。</summary>
    public async Task EnsureMonthlySummaryFreshAsync(int userId, int year, int month)
    {
        var summary = await db.MonthlyAttendanceSummaries
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Year == year && s.Month == month);
        if (summary is null)
        {
            await GenerateMonthlySummaryAsync(year, month, [userId]);
            return;
        }

        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);
        var latestRecordUpdate = await db.AttendanceRecords
            .Where(r => r.UserId == userId && r.WorkDate >= start && r.WorkDate <= end)
            .Select(r => (DateTime?)r.UpdatedAt)
            .MaxAsync() ?? DateTime.MinValue;
        if (latestRecordUpdate > summary.UpdatedAt)
            await GenerateMonthlySummaryAsync(year, month, [userId]);
    }
}
