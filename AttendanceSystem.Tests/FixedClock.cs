namespace AttendanceSystem.Tests;

/// <summary>固定在某个本地时刻的时钟，测试"现在几点"相关的规则用（本地时区 = 该时刻本身，不跟机器时区走）。</summary>
public sealed class FixedClock(DateTime localNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(localNow, DateTimeKind.Utc));
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
