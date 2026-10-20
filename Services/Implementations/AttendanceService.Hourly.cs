using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 计时工模式（<see cref="AttendanceGroup.HourlyWorkStartDate"/>）的公共方法：是不是计时工、打卡类型怎么判、工时怎么算。
/// 所有改动都只在"这个考勤组开启了计时工模式、且考勤日不早于起始日"时才生效，没开的考勤组走原来的逻辑。
/// </summary>
public partial class AttendanceService
{
    /// <summary>计时工每天最早有效打卡时刻（含）：00:00–07:00 之间的打卡无效，防止刷到凌晨。</summary>
    public const int HourlyEarliestPunchHour = 7;

    public const string HourlyInvalidTimeMessage = "计时工 00:00 到 07:00 之间的打卡无效，请 07:00 以后再打卡";

    /// <summary>所有开启了计时工模式的考勤组：考勤组 Id → 起始日。</summary>
    public async Task<Dictionary<int, DateOnly>> LoadHourlyGroupStartsAsync() =>
        await db.AttendanceGroups.Where(g => g.HourlyWorkStartDate != null)
            .ToDictionaryAsync(g => g.Id, g => g.HourlyWorkStartDate!.Value);

    /// <summary>这个考勤组在这个考勤日是不是按计时工模式算。</summary>
    public static bool IsHourlyOn(IReadOnlyDictionary<int, DateOnly> starts, int? groupId, DateOnly workDate) =>
        groupId is { } g && starts.TryGetValue(g, out var from) && workDate >= from;

    public async Task<bool> IsHourlyOnAsync(int? groupId, DateOnly workDate)
    {
        if (groupId is not { } g) return false;
        var from = await db.AttendanceGroups.Where(x => x.Id == g).Select(x => x.HourlyWorkStartDate).FirstOrDefaultAsync();
        return from is { } f && workDate >= f;
    }

    /// <summary>计时工的有效打卡时刻：07:00（含）之后、当天 23:59 之前。</summary>
    public static bool IsHourlyPunchTimeValid(DateTime punchTime) => punchTime.Hour >= HourlyEarliestPunchHour;

    /// <summary>
    /// 计时工的打卡类型（远程打卡页和考勤机同步共用）：当天还没有上班卡 → 上班；落在午间窗口里（含窗口结束后 30 分钟宽限）→ 午间；
    /// 其余都是下班卡（取当天最晚的一次）。计时工下班时间不固定，不能按"应下班前 2 小时才算下班"来判断。
    /// </summary>
    public static PunchType ResolveHourlyPunchType(DateTime punchTime, DateOnly workDate, ShiftSchedule? shift, bool hasClockIn)
    {
        if (!hasClockIn) return PunchType.ClockIn;
        var windows = shift?.ParseMidCheckWindows() ?? [];
        for (var i = 0; i < windows.Count; i++)
            if (punchTime >= ResolveShiftTime(workDate, windows[i].Start, shift!)
                && punchTime <= MidCheckEffectiveEnd(workDate, shift!, windows, i))
                return PunchType.MidCheck;
        return PunchType.ClockOut;
    }

    /// <summary>
    /// 计时工的工时：最后一次下班卡 − 第一次上班卡，扣午饭/晚饭（<see cref="ComputeWorkHours"/> 按默认饭点重叠扣减），
    /// 不按班次标准工时封顶、不把早到晚走裁剪到班次时间；午间窗口漏打：只算窗口开始之前
    /// （跟原来的 <see cref="ResolveSecondHalfAbsentBoundary"/> 同一口径）；当天 23:59:59 之后的时间不算。
    /// </summary>
    public static decimal ComputeHourlyWorkHours(DateOnly workDate, DateTime clockIn, DateTime clockOut, ShiftSchedule? shift, List<MidCheckWindowResult>? midResults)
    {
        var effIn = clockIn;
        var effOut = clockOut;
        if (shift is not null && midResults is { Count: > 0 })
        {
            foreach (var missedEnd in ResolveMissedNonLastWindowEnds(workDate, shift, midResults))
                if (missedEnd > effIn) effIn = missedEnd;
            if (ResolveSecondHalfAbsentBoundary(workDate, shift, midResults) is { } boundary && boundary < effOut)
                effOut = boundary;
        }
        var dayEnd = workDate.ToDateTime(new TimeOnly(23, 59, 59));
        if (effOut > dayEnd) effOut = dayEnd;
        return effOut <= effIn ? 0 : ComputeWorkHours(effIn, effOut);
    }

    /// <summary>
    /// 计时工：上班卡照常判断迟到（只记录，不影响工时）；下班时间不固定，没有早退；没有旷工、缺卡：
    /// 早退/旷工/未打卡的状态改成"正常"，早退分钟数清零（出差、请假、节假日这些审批定的状态不动）。
    /// </summary>
    public static void NormalizeHourlyRecord(AttendanceRecord record)
    {
        record.EarlyLeaveMinutes = 0;
        if (record.AttendanceStatus is AttendanceStatus.EarlyLeave or AttendanceStatus.Absent or AttendanceStatus.NotPunched)
            record.AttendanceStatus = AttendanceStatus.Normal;
    }
}
