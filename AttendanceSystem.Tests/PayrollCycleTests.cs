using AttendanceSystem.Helpers;

namespace AttendanceSystem.Tests;

/// <summary>薪资周期：每月 26 日 ~ 下月 25 日，按结束月份命名；"我的记录/我的日历"据此显示。</summary>
public class PayrollCycleTests
{
    [Fact]
    public void 十月周期_是9月26日到10月25日()
    {
        var (start, end) = PayrollCycle.Range(2026, 10);
        Assert.Equal(new DateOnly(2026, 9, 26), start);
        Assert.Equal(new DateOnly(2026, 10, 25), end);
    }

    [Fact]
    public void 一月周期_跨年_是上一年12月26日到1月25日()
    {
        var (start, end) = PayrollCycle.Range(2027, 1);
        Assert.Equal(new DateOnly(2026, 12, 26), start);
        Assert.Equal(new DateOnly(2027, 1, 25), end);
    }

    [Theory]
    [InlineData(2026, 9, 25, 2026, 9)]     // 25 号还在 9 月周期（8/26~9/25）
    [InlineData(2026, 9, 26, 2026, 10)]    // 26 号起算 10 月周期
    [InlineData(2026, 10, 8, 2026, 10)]
    [InlineData(2026, 12, 31, 2027, 1)]    // 年底进下一年的 1 月周期
    public void 某一天属于哪个周期(int y, int m, int d, int endY, int endM)
        => Assert.Equal((endY, endM), PayrollCycle.EndMonthOf(new DateOnly(y, m, d)));

    [Fact]
    public void 网址里的年月不合法或太远_退回当前周期()
    {
        var today = new DateOnly(2026, 10, 8);
        Assert.Equal((2026, 10), PayrollCycle.ResolveRequested(null, null, today));
        Assert.Equal((2026, 10), PayrollCycle.ResolveRequested(2026, 13, today));
        Assert.Equal((2026, 10), PayrollCycle.ResolveRequested(1999, 5, today));
        Assert.Equal((2026, 10), PayrollCycle.ResolveRequested(2020, 1, today));   // 超过 24 个月
        Assert.Equal((2026, 9), PayrollCycle.ResolveRequested(2026, 9, today));
        Assert.Equal((2026, 11), PayrollCycle.ResolveRequested(2026, 11, today)); // 下个周期放开
    }
}
