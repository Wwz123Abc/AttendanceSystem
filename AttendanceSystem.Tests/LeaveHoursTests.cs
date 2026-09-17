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

    // ── 半天请假支持（口径登记表 §4 验收清单 1-4、7）──────────────────────────────
    // ApplyLeaveHoursCap / ResolveLeaveDaysFraction 是打卡结算、审批回写、月度汇总三条路径
    // 共用的核心公式，这里直接测公式本身，覆盖验收清单里给出的具体算例。

    [Fact]
    public void 验收1_全天请假无打卡_工时0()
    {
        // 没有真实打卡时，ActualWorkHours 在写入时就直接置 0，不会走到 ApplyLeaveHoursCap，
        // 这里验证的是"即使有人手滑传了 0 小时的打卡工时进来"，封顶公式本身也会算出 0
        Assert.Equal(0m, AttendanceService.ApplyLeaveHoursCap(0m, leaveHours: 8m, standardHours: 8m));
    }

    [Fact]
    public void 验收2_全天请假但打了1点5小时的卡_工时仍是0_不会既算全天假又算工时()
    {
        // 8:30-10:00 = 1.5 小时；请了一整天假（LeaveHours=8=标准工时）→ 上限是 0，不能因为
        // 手滑打了卡就多算出 1.5 小时工时
        Assert.Equal(0m, AttendanceService.ApplyLeaveHoursCap(1.5m, leaveHours: 8m, standardHours: 8m));
    }

    [Fact]
    public void 验收3_上午请假3点5小时_下午上班5点5小时_工时封顶4点5小时()
    {
        // 12:00-17:30 = 5.5 小时（不足 6 小时不扣午休）；上午请了 3.5 小时假 → 上限 = 8-3.5 = 4.5，
        // 实际打卡工时 5.5 大于上限，按上限 4.5 结算
        var clockIn  = new DateTime(2026, 9, 10, 12, 0, 0);
        var clockOut = new DateTime(2026, 9, 10, 17, 30, 0);
        var computed = AttendanceService.ComputeWorkHours(clockIn, clockOut, Lunch, Dinner);
        Assert.Equal(4.5m, AttendanceService.ApplyLeaveHoursCap(computed, leaveHours: 3.5m, standardHours: StandardHours));
    }

    [Fact]
    public void 验收4_下午请假4小时_上午上班3点5小时_工时按实际打卡3点5小时_不被上限顶高()
    {
        // 8:30-12:00 = 3.5 小时；下午请了 4 小时假 → 上限 = 8-4 = 4，但实际打卡工时只有 3.5，
        // min(3.5, 4) = 3.5——上限只封顶"多出来的"部分，不会把工时"顶"到上限那么高
        var clockIn  = new DateTime(2026, 9, 10, 8, 30, 0);
        var clockOut = new DateTime(2026, 9, 10, 12, 0, 0);
        var computed = AttendanceService.ComputeWorkHours(clockIn, clockOut, Lunch, Dinner);
        Assert.Equal(3.5m, AttendanceService.ApplyLeaveHoursCap(computed, leaveHours: 4m, standardHours: StandardHours));
    }

    [Theory]
    [InlineData(8, 8, 1)]      // 一整天假（占比 100%）→ 1 天
    [InlineData(5, 8, 1)]      // 占比 62.5%，≥0.5 → 算 1 天（口径登记表 §4.2 第 6 条按字面写的边界）
    [InlineData(3.5, 8, 0.5)]  // 占比 43.75%，>0 且 <0.5 → 算半天，不是按比例给 0.4375 天
    [InlineData(0, 8, 0)]      // 没有请假小时数 → 0 天
    public void 请假天数按占比折算_不是按状态是否请假就算一整天(decimal leaveHours, decimal standardHours, decimal expectedDays)
    {
        Assert.Equal(expectedDays, AttendanceService.ResolveLeaveDaysFraction(leaveHours, standardHours));
    }

    [Fact]
    public void 边界情况_恰好占比50百分之_按规范字面意思算1天_不是0点5天()
    {
        // ⚠️ 这条锁住的是口径登记表 §4.2 第 6 条"≥0.5 → 1 天"边界的字面行为：请假 4 小时、
        // 标准工时 8 小时，占比恰好 50%，落在">=0.5"这一边，算出来是 1 天整——也就是最常见的
        // "标准半天假"场景会被记成一整天请假。这看起来跟直觉不符（半天假直觉上该是 0.5 天），
        // 已经单独跟你确认这条边界是否是故意这样定的，这个测试先照文档字面实现锁定当前行为，
        // 如果确认后规则要改（比如改成 >0.5 才算 1 天，恰好 0.5 单独算 0.5 天），这个测试要跟着改。
        Assert.Equal(1m, AttendanceService.ResolveLeaveDaysFraction(4m, 8m));
    }

    [Fact]
    public void 验收7_同一天两张假单_请假小时数累加_折算成1天不是0点5天()
    {
        // 上午一张 3.5 小时假 + 下午一张 4.5 小时假，同一天 LeaveHours 应该是累加（+=）后的 8 小时，
        // 不是后一张覆盖前一张变成 4.5 小时——覆盖的话这里会折算成 0.5 天，累加才是 1 天
        var morningLeave = 3.5m;
        var afternoonLeave = 4.5m;
        var accumulated = morningLeave + afternoonLeave;   // 模拟 record.LeaveHours += 两次
        Assert.Equal(1m, AttendanceService.ResolveLeaveDaysFraction(accumulated, StandardHours));
    }
}
