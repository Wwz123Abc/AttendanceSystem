using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>午间打卡的宽限（2026-10-20 业务口径）：窗口结束后 30 分钟内打卡仍算命中、不扣工时；超过 30 分钟算漏打。</summary>
public class MidCheckGraceTests
{
    private static readonly DateOnly Tue = new(2026, 9, 8);
    private static DateTime At(int h, int m) => Tue.ToDateTime(new TimeOnly(h, m));
    private static ShiftSchedule Day() => new() { WorkStartTime = new TimeOnly(8, 30), WorkEndTime = new TimeOnly(17, 30), IsCrossDay = false };
    private static List<(TimeOnly Start, TimeOnly End)> One() => [(new TimeOnly(12, 0), new TimeOnly(13, 0))];

    [Theory]
    [InlineData(12, 30, true)]    // 窗口内
    [InlineData(13, 0, true)]     // 窗口结束那一刻
    [InlineData(13, 20, true)]    // 迟到 20 分钟，不扣
    [InlineData(13, 30, true)]    // 迟到整 30 分钟，不扣
    [InlineData(13, 31, false)]   // 超过 30 分钟，算漏打
    [InlineData(11, 59, false)]   // 窗口开始之前不算
    public void 午间卡的命中边界(int h, int m, bool expectHit)
    {
        var r = Assert.Single(AttendanceService.ResolveMidCheckResults(Tue, Day(), One(), [At(h, m)]));
        Assert.Equal(expectHit, r.HitTime.HasValue);
    }

    [Fact]
    public void 宽限不越过下一段窗口的开始_相邻窗口各算各的()
    {
        List<(TimeOnly Start, TimeOnly End)> two = [(new TimeOnly(10, 0), new TimeOnly(10, 20)), (new TimeOnly(10, 30), new TimeOnly(11, 0))];
        // 10:35 在第二段窗口里：第一段不能把它抢走（第一段宽限只到 10:29:59），第二段命中
        var r = AttendanceService.ResolveMidCheckResults(Tue, Day(), two, [At(10, 35)]);
        Assert.Null(r[0].HitTime);
        Assert.Equal(new TimeOnly(10, 35), r[1].HitTime);
        // 10:25 在第一段的宽限里，命中第一段
        var r2 = AttendanceService.ResolveMidCheckResults(Tue, Day(), two, [At(10, 25)]);
        Assert.Equal(new TimeOnly(10, 25), r2[0].HitTime);
    }
}
