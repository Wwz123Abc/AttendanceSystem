using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 覆盖 <see cref="AttendanceService.ComputeLeaveHoursForDay"/>（2026-09-17 代码审查发现并修复）：
/// 请假时长以前直接拿"这一天和请假区间的交集"套用工时公式，跨天请假中间那几天会把整晚睡眠时间
/// 也算进请假时长，一天能算出 22.5 小时这种荒谬数字，且跟提交时的整段估算对不上。
/// 修复后按标准工时封顶，这里锁住整天/跨天/半天三种情形的期望值。
/// </summary>
public class LeaveHoursTests
{
    private const int Lunch  = 60;
    private const int Dinner = 30;
    private const decimal StandardHours = 8m;   // 标准工时按 8 小时算

    [Fact]
    public void 请一整天假_按标准工时封顶_不是按午夜到午夜的公式算出22点5小时()
    {
        var day   = new DateOnly(2026, 9, 10);
        var start = day.ToDateTime(TimeOnly.MinValue);          // 当天 00:00
        var end   = day.AddDays(1).ToDateTime(TimeOnly.MinValue); // 次日 00:00（跨天请假的中间整天）

        var hours = AttendanceService.ComputeLeaveHoursForDay(day, start, end, Lunch, Dinner, StandardHours);

        Assert.Equal(StandardHours, hours);   // 封顶在标准工时，不是 22.5
    }

    [Fact]
    public void 跨天请假_三天累计不再是套整段公式算出的畸高总数()
    {
        // 09-09 14:00 请假到 09-11 12:00（报告里复现的真实算例）
        var start = new DateTime(2026, 9, 9, 14, 0, 0);
        var end   = new DateTime(2026, 9, 11, 12, 0, 0);

        var day1 = AttendanceService.ComputeLeaveHoursForDay(new DateOnly(2026, 9, 9),  start, end, Lunch, Dinner, StandardHours);
        var day2 = AttendanceService.ComputeLeaveHoursForDay(new DateOnly(2026, 9, 10), start, end, Lunch, Dinner, StandardHours);
        var day3 = AttendanceService.ComputeLeaveHoursForDay(new DateOnly(2026, 9, 11), start, end, Lunch, Dinner, StandardHours);

        // 中间那天（09-10）是完整的一天，必须被封顶在标准工时，不能是 22.5
        Assert.Equal(StandardHours, day2);
        // 三天总和不能超过"三天标准工时"，不会再出现套整段公式算出的 44.5 这种远超三天标准工时的数字
        Assert.True(day1 + day2 + day3 <= StandardHours * 3);
    }

    [Fact]
    public void 半天请假_按实际时长扣午休_不封顶到标准工时以上()
    {
        var day   = new DateOnly(2026, 9, 10);
        var start = day.ToDateTime(new TimeOnly(9, 0));
        var end   = day.ToDateTime(new TimeOnly(13, 0));   // 上午请假 9:00-13:00，4 小时，不足 6 小时不扣午休

        var hours = AttendanceService.ComputeLeaveHoursForDay(day, start, end, Lunch, Dinner, StandardHours);

        Assert.Equal(4m, hours);
    }

    [Fact]
    public void 请假区间在这天之外_返回0()
    {
        var day   = new DateOnly(2026, 9, 10);
        var start = new DateTime(2026, 9, 8, 9, 0, 0);
        var end   = new DateTime(2026, 9, 9, 18, 0, 0);   // 整段区间都在 09-10 之前

        var hours = AttendanceService.ComputeLeaveHoursForDay(day, start, end, Lunch, Dinner, StandardHours);

        Assert.Equal(0m, hours);
    }
}
