using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>午间打卡"一段窗口"的判定结果：这段要求的时间范围，以及当天有没有打上（打上了是几点）。</summary>
/// <param name="LateTime">窗口过去之后才打的那次午间打卡的时间（只用于展示"迟到"；没打上窗口就是没打上，
/// 工时按"午间漏打"算，不受它影响；存库字符串里没有这一项，查询时现算）。</param>
public readonly record struct MidCheckWindowResult(TimeOnly WindowStart, TimeOnly WindowEnd, TimeOnly? HitTime, TimeOnly? LateTime = null)
{
    public bool IsSatisfied => HitTime is not null;

    /// <summary>没在窗口内打上，但窗口结束之后补打过——展示成"迟到"。</summary>
    public bool IsLate => HitTime is null && LateTime is not null;

    /// <summary>迟到了几分钟（打卡时间 − 窗口结束时间）；不是迟到返回 0。</summary>
    public int LateMinutes => IsLate ? Math.Max(0, (int)(LateTime!.Value.ToTimeSpan() - WindowEnd.ToTimeSpan()).TotalMinutes) : 0;
}
