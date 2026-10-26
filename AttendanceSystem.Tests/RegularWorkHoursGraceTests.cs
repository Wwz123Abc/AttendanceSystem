using AttendanceSystem.Helpers;
using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>正班工时取整前加 5 分钟宽限（2026-10-26）：汤晓永 9/30 上班卡 08:30:33、按 17:30 下班，
/// 在岗 7 小时 59 分 27 秒被取整成 7.5，差几十秒吃掉半小时。只用于打卡算正班工时，加班、请假不套。</summary>
[Collection("UnpaidBreakStatic")]
public class RegularWorkHoursGraceTests : IDisposable
{
    public void Dispose() => UnpaidBreakSettings.ResetAll();

    private static DateTime T(int h, int m, int s = 0) => new(2026, 9, 30, h, m, s);

    [Fact]
    public void 上班卡晚几十秒_正班满八小时_不再被取整成七点五()
    {
        Assert.Equal(8m, AttendanceService.ComputeRegularWorkHours(T(8, 30, 33), T(17, 30)));
        Assert.Equal(7.5m, AttendanceService.ComputeWorkHours(T(8, 30, 33), T(17, 30)));   // 原公式不变（加班、请假仍用它）
    }

    [Theory]
    [InlineData(8, 35, 0, 8.0)]    // 在岗 7h55m，加 5 分钟刚好 8 小时
    [InlineData(8, 35, 1, 7.5)]    // 在岗 7h54m59s，加 5 分钟还差 1 秒
    [InlineData(8, 30, 0, 8.0)]
    [InlineData(9, 0, 0, 7.5)]     // 在岗 7h30m，宽限不够补到 8 小时
    public void 宽限边界_差五分钟以内算整半小时(int h, int m, int s, double expected)
        => Assert.Equal((decimal)expected, AttendanceService.ComputeRegularWorkHours(T(h, m, s), T(17, 30)));

    [Fact]
    public void 零长度和倒挂的打卡仍是零_宽限不会凭空变出工时()
    {
        Assert.Equal(0m, AttendanceService.ComputeRegularWorkHours(T(8, 30), T(8, 30)));
        Assert.Equal(0m, AttendanceService.ComputeRegularWorkHours(T(9, 0), T(8, 0)));
        Assert.Equal(0m, AttendanceService.ComputeRegularWorkHours(T(12, 5), T(12, 50)));   // 整段在午饭时段里
    }

    [Fact]
    public void 宽限只加一次_不会把半小时零头连环进位()
    {
        // 13:00–17:03 在岗 4h03m，加 5 分钟 4h08m，仍取 4.0，不会变成 4.5
        Assert.Equal(4.0m, AttendanceService.ComputeRegularWorkHours(T(13, 0), T(17, 3)));
    }
}
