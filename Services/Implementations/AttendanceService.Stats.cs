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

/// <summary>今日统计、迟到早退口径、出勤天数折算、休息日判断（<see cref="AttendanceService"/> 的一部分）。</summary>
public partial class AttendanceService
{
    /// <summary>今日考勤看板统计（出勤/旷工/迟到/请假/未打卡人数）。</summary>
    public async Task<AttendanceStatsDto> GetTodayStatsAsync(int? groupId = null, HashSet<int>? deptIds = null, CancellationToken ct = default)
    {
        var today   = DateOnly.FromDateTime(clock.LocalToday());
        var userIds = await BuildUserIdQueryAsync(null, groupId, deptIds, excludeExempt: true);
        var records = await db.AttendanceRecords
            .Where(r => r.WorkDate == today && userIds.Contains(r.UserId))
            .ToListAsync(ct);

        // "没出勤"的人里，旷工/请假/节假日已经各有自己的口径和卡片了，"未打卡"这张卡只应该统计
        // 剩下那批"今天还没来打卡、但又不属于旷工/请假/节假日"的人（比如上午还没到岗），
        // 不然旷工当天晚上 23:55 被后台任务标记成 Absent 之后，同一个人会同时被"旷工"和"未打卡"
        // 两张卡各数一遍，两个数字加起来会比总人数还多，看板数据对不上。
        // 只数"没出勤"的：半天假当天打了上班卡的记录同时是"出勤"和"请假"，两边都减一遍会把"未打卡"扣少
        // （卡片数小于下钻名单、人少时还可能出现负数，跟 GetTodayStatsDetailAsync 的 notpunched 名单对不上）
        var accountedForCount = records.Count(r => !IsPresent(r) &&
            r.AttendanceStatus is AttendanceStatus.Absent or AttendanceStatus.OnLeave or AttendanceStatus.Holiday);

        return new AttendanceStatsDto
        {
            StatsDate       = today,
            TotalEmployees  = userIds.Count,
            PresentCount    = records.Count(IsPresent),   // 打了上班卡，或已批准出差（无需打卡也算全勤）
            AbsentCount     = records.Count(r => r.AttendanceStatus == AttendanceStatus.Absent),
            // 迟到按「状态」统计（与缺勤/请假口径一致）：钉钉同步只写状态不写迟到分钟数，
            // 若按 LateMinutes>0 算会漏掉钉钉来的迟到。
            LateCount       = records.Count(r => r.AttendanceStatus == AttendanceStatus.Late),
            OnLeaveCount    = records.Count(r => r.AttendanceStatus == AttendanceStatus.OnLeave),
            // 总人数 - 出勤 - 旷工/请假/节假日 = 剩下"还没打卡、原因待定"的人，不和旷工/请假重复计数
            NotPunchedCount = userIds.Count - records.Count(IsPresent) - accountedForCount,
            LocationAbnormalCount = records.Count(r => r.LocationAbnormal)
        };
    }

    /// <summary>
    /// 这条记录算数的"迟到分钟"：只有状态本身就是"迟到"才算，否则一律 0。
    /// 数据库里的 LateMinutes 是"打上班卡那一刻算出来的值"，之后当天状态可能被改成别的（后台判成"未打卡"、
    /// 请假审批回写、补卡后重算……），分钟数不一定同步清掉，直接拿来汇总会跟"迟到次数"（按状态数）对不上。
    /// 所有展示和汇总的迟到分钟都走这个方法，保证逐日相加 = 合计，分钟和次数同一个口径。
    /// </summary>
    public static int EffectiveLateMinutes(AttendanceRecord r) => EffectiveLateMinutes(r.AttendanceStatus, r.LateMinutes);

    /// <summary>同上，给只投影了"状态 + 分钟数"、手上没有完整记录对象的查询用（比如 AGENT 的异常清单）。</summary>
    public static int EffectiveLateMinutes(AttendanceStatus status, int lateMinutes) =>
        status == AttendanceStatus.Late ? lateMinutes : 0;

    /// <summary>这条记录算数的"早退分钟"：只有状态本身就是"早退"才算，理由同 <see cref="EffectiveLateMinutes(AttendanceRecord)"/>。</summary>
    public static int EffectiveEarlyLeaveMinutes(AttendanceRecord r) => EffectiveEarlyLeaveMinutes(r.AttendanceStatus, r.EarlyLeaveMinutes);

    public static int EffectiveEarlyLeaveMinutes(AttendanceStatus status, int earlyLeaveMinutes) =>
        status == AttendanceStatus.EarlyLeave ? earlyLeaveMinutes : 0;

    /// <summary>判断某天算不算“出勤”：打了上班卡，或者当天已批准出差（出差无需打卡也算全勤）。</summary>
    private static bool IsPresent(AttendanceRecord r) => r.ClockInTime.HasValue || r.AttendanceStatus == AttendanceStatus.BusinessTrip;

    /// <summary>
    /// 这一天该算多少"出勤天数"：完全没到岗（含整天请假）算 0；请假当天如果也有真实打卡（半天假），
    /// 按"1 − 请假占比"算部分出勤，不再跟请假天数重复记满整天；其余（含出差全勤）算 1 整天。
    /// ★ 全系统唯一口径：<see cref="GenerateMonthlySummaryAsync"/>（月度汇总/我的记录页）和
    /// <see cref="GenerateTemplateReportAsync"/>（模板汇总表，发工资用的那份导出）必须共用这一个方法，
    /// 不能各写一份——之前就是因为两处各算各的，同一个人同一个月两份报表的出勤天数对不上
    /// （发现于 2026-09-18 数据核查）。
    /// </summary>
    public static decimal ResolveAttendanceDayCredit(AttendanceRecord r, decimal standardHours, bool isRestDay = false)
    {
        if (!IsPresent(r)) return 0m;
        // 休息日/节假日来打卡：不算"出勤天数"（这天没有应出勤，正班工时也是 0；批了加班的，加班时长单独在加班里体现）。
        // 不然出勤天数会超过应出勤天数，按出勤天数发的全勤奖/补贴会多发（2026-09-28 你定的口径）。请假当天不受影响
        if (isRestDay && r.AttendanceStatus != AttendanceStatus.OnLeave) return 0m;
        if (r.AttendanceStatus != AttendanceStatus.OnLeave) return 1m;
        return Math.Max(0m, 1m - ResolveLeaveDaysFraction(r.LeaveHours, standardHours));
    }

    /// <summary>
    /// 看板下钻：某统计类别对应的具体人员名单。分类口径和 <see cref="GetTodayStatsAsync"/> 完全一致
    /// （同一批 userIds、同一批 records、同样的判断条件），保证卡片上的数字和点开后名单的人数永远对得上。
    /// </summary>
    public async Task<List<AttendanceRecordDto>> GetTodayStatsDetailAsync(string category, int? groupId = null, HashSet<int>? deptIds = null, CancellationToken ct = default)
    {
        var today   = DateOnly.FromDateTime(clock.LocalToday());
        var userIds = await BuildUserIdQueryAsync(null, groupId, deptIds, excludeExempt: true);   // 必须和 GetTodayStatsAsync 同一批人

        var users = await db.Users.Include(u => u.Department)
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var records = await db.AttendanceRecords
            .Where(r => r.WorkDate == today && userIds.Contains(r.UserId))
            .ToListAsync(ct);
        var recordByUser = records.ToDictionary(r => r.UserId);

        // 挑出属于该类别的用户 id：判断条件必须和 GetTodayStatsAsync 里一一对应，否则数字会对不上
        var presentIds = records.Where(IsPresent).Select(r => r.UserId).ToHashSet();
        List<int> targetIds = category switch
        {
            "total"      => userIds,
            "present"    => presentIds.ToList(),
            "absent"     => records.Where(r => r.AttendanceStatus == AttendanceStatus.Absent).Select(r => r.UserId).ToList(),
            "late"       => records.Where(r => r.AttendanceStatus == AttendanceStatus.Late).Select(r => r.UserId).ToList(),
            "onleave"    => records.Where(r => r.AttendanceStatus == AttendanceStatus.OnLeave).Select(r => r.UserId).ToList(),
            // 含"完全没记录"和"有记录但没打上班卡"两种人，但排除旷工/请假/节假日——这三类已经各有
            // 自己的卡片，混进"未打卡"会跟"absent"下钻名单里的人重复，和 GetTodayStatsAsync 的口径保持一致
            "notpunched" => userIds.Where(id => !presentIds.Contains(id)
                && (!recordByUser.TryGetValue(id, out var npRec)
                    || npRec.AttendanceStatus is not (AttendanceStatus.Absent or AttendanceStatus.OnLeave or AttendanceStatus.Holiday)))
                .ToList(),
            "locationabnormal" => records.Where(r => r.LocationAbnormal).Select(r => r.UserId).ToList(),
            _            => []
        };

        var result = new List<AttendanceRecordDto>();
        foreach (var uid in targetIds)
        {
            if (!users.TryGetValue(uid, out var user)) continue;   // 理论上不会发生，防御一下
            recordByUser.TryGetValue(uid, out var rec);

            result.Add(new AttendanceRecordDto
            {
                Id               = rec?.Id ?? 0,
                UserId           = uid,
                EmployeeNo       = user.EmployeeNo,
                RealName         = user.RealName,
                DeptName         = user.Department?.DeptName,
                WorkDate         = today,
                ClockInTime      = rec?.ClockInTime,
                ClockOutTime     = rec?.ClockOutTime,
                AttendanceStatus = rec?.AttendanceStatus ?? AttendanceStatus.NotPunched,
                StatusText       = rec is null ? "未打卡（无记录）" : StatusText(rec.AttendanceStatus),
                StatusCssClass   = rec is null ? "f-color-red" : StatusCss(rec.AttendanceStatus),
                LateMinutes      = rec is null ? 0 : EffectiveLateMinutes(rec),
                EarlyLeaveMinutes = rec is null ? 0 : EffectiveEarlyLeaveMinutes(rec),
                ApprovalNote     = rec?.ApprovalNote,
                LocationAbnormal     = rec?.LocationAbnormal ?? false,
                LocationAbnormalNote = rec?.LocationAbnormalNote
            });
        }

        return result
            .OrderBy(r => r.DeptName ?? "")
            .ThenBy(r => r.EmployeeNo)
            .ToList();
    }

    /// <summary>
    /// 这天是不是排的班次自己配置的每周休息日；没排班时按全局周六周日兜底。
    /// ★ 全系统唯一口径：<see cref="CountExpectedWorkdaysAsync"/>、<see cref="GenerateTemplateReportAsync"/>、
    /// <see cref="IsNonCompRestDayAsync"/>、AttendanceBackgroundService.MarkAbsentAsync 都调这一个，
    /// 避免同一条"是不是休息日"的规则散落成好几份、以后改规则漏改一处。
    /// </summary>
    public static bool IsShiftWeeklyRestDay(DateOnly date, ShiftSchedule? shift) =>
        shift is not null ? shift.IsRestDay(date.DayOfWeek) : date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>判断某天对这个员工算不算"休息日"——休息日的正班工时一律记 0（加班只认审批单），不影响能不能打卡。
    /// 项目不再区分法定节假日/公司休息/调班补班（2026-09-30 用户确认整体去掉节假日功能），
    /// 只按班次自己配置的每周休息日判断，等价于 <see cref="IsShiftWeeklyRestDay"/>。</summary>
    public static bool IsNonCompRestDay(DateOnly date, ShiftSchedule? shift) => IsShiftWeeklyRestDay(date, shift);
}
