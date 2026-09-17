using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 覆盖"午间打卡漏打 → 有效上/下班时间怎么算"这条链路（ResolveMissedNonLastWindowEnds /
/// ResolveSecondHalfAbsentBoundary / ClampEffectiveClockIn / ClampEffectiveClockOut）。
/// 这条链路 2026-09-17 一天之内被发现过 3 个真实生产 bug（午间打卡误判早退、漏打最后一段
/// 导致整天工时清零、清零边界用错窗口结束时间而不是开始时间），本身又没有任何自动化测试，
/// 这里把这几个已修复的真实场景固化成回归测试，防止以后改动时悄悄改回错的行为。
/// </summary>
public class MidCheckWindowLogicTests
{
    private static readonly DateOnly WorkDate = new(2026, 9, 17);

    private static ShiftSchedule DayShift(TimeOnly start, TimeOnly end, bool crossDay = false) => new()
    {
        WorkStartTime = start,
        WorkEndTime   = end,
        IsCrossDay    = crossDay
    };

    // ── 复现"09-15/09-16 那 20 条记录被整天清零"的真实场景 ─────────────────────────
    // 08:30 上班、20:00 下班，中间配了一段 12:00-13:00 的午间打卡，员工没打这一段。
    [Fact]
    public void 漏打唯一的午间窗口_只清掉下午_不清掉整天()
    {
        var shift = DayShift(new TimeOnly(8, 30), new TimeOnly(20, 0));
        var results = new List<MidCheckWindowResult>
        {
            new(new TimeOnly(12, 0), new TimeOnly(13, 0), null)   // 唯一一段，且没打上
        };

        // 唯一的一段窗口同时也是"最后一段"，它漏打的后果应该只由 ResolveSecondHalfAbsentBoundary
        // 处理（下午不算工时），不应该出现在"顺延上班时间"的列表里——否则上班时间也会被推到 13:00，
        // 跟下班边界撞在一起，变成整天工时为 0（这正是当时的真实 bug）。
        var missedNonLast = AttendanceService.ResolveMissedNonLastWindowEnds(WorkDate, shift, results);
        Assert.Empty(missedNonLast);

        var boundary = AttendanceService.ResolveSecondHalfAbsentBoundary(WorkDate, shift, results);
        Assert.Equal(WorkDate.ToDateTime(new TimeOnly(12, 0)), boundary);   // 用窗口开始时间，不是结束时间

        var clockIn  = WorkDate.ToDateTime(new TimeOnly(8, 30));
        var clockOut = WorkDate.ToDateTime(new TimeOnly(20, 0));
        var effIn  = AttendanceService.ClampEffectiveClockIn(WorkDate, clockIn, shift, missedNonLast);
        var effOut = AttendanceService.ClampEffectiveClockOut(WorkDate, clockOut, shift, boundary);

        Assert.Equal(clockIn, effIn);                                  // 上班时间不受影响
        Assert.Equal(WorkDate.ToDateTime(new TimeOnly(12, 0)), effOut); // 下班收窄到午休开始

        // 跟用户在事故复盘时口算的一致：早上 8:30~12:00 = 3.5 小时，午休及以后一律不算
        var hours = AttendanceService.ComputeWorkHours(effIn, effOut, lunchBreak: 60, dinnerBreak: 30);
        Assert.Equal(3.5m, hours);
    }

    [Fact]
    public void 漏打的不是最后一段_正常顺延上班时间_不影响下班()
    {
        // 配两段：10:00-10:10（茶歇，漏打）、12:00-13:00（午休，正常打了）
        var shift = DayShift(new TimeOnly(8, 30), new TimeOnly(20, 0));
        var results = new List<MidCheckWindowResult>
        {
            new(new TimeOnly(10, 0), new TimeOnly(10, 10), null),                 // 没打上
            new(new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(12, 30))   // 打上了
        };

        var missedNonLast = AttendanceService.ResolveMissedNonLastWindowEnds(WorkDate, shift, results);
        Assert.Equal([WorkDate.ToDateTime(new TimeOnly(10, 10))], missedNonLast);

        // 最后一段（12:00-13:00）已经打上了，不该有"下午不算工时"的边界
        var boundary = AttendanceService.ResolveSecondHalfAbsentBoundary(WorkDate, shift, results);
        Assert.Null(boundary);
    }

    // ── 两段窗口"结束时间恰好相同"时，两个函数必须认定同一段是"最后一段" ────────────
    [Fact]
    public void 两段窗口结束时间相同_都漏打时_只有一段被算作非最后段_不会被两边都漏掉或都排除()
    {
        var shift = DayShift(new TimeOnly(8, 30), new TimeOnly(20, 0));
        var results = new List<MidCheckWindowResult>
        {
            new(new TimeOnly(12, 0), new TimeOnly(13, 0), null),   // 窗口 A：12:00-13:00，没打上
            new(new TimeOnly(12, 30), new TimeOnly(13, 0), null)   // 窗口 B：12:30-13:00，结束时间和 A 撞了，也没打上
        };

        var missedNonLast = AttendanceService.ResolveMissedNonLastWindowEnds(WorkDate, shift, results);
        // 关键断言：修复前是"排除所有 WindowEnd == 最大值的窗口"，两段都会被排除，返回空列表，
        // 等于窗口 A 漏打这件事完全没人处理；修复后应该恰好排除其中一段（被判定为"最后一段"的
        // 那一个），另一段仍然计入"漏打非最后段"列表。
        Assert.Single(missedNonLast);

        var boundary = AttendanceService.ResolveSecondHalfAbsentBoundary(WorkDate, shift, results);
        Assert.NotNull(boundary);   // 两段都漏打，"最后一段"那一个必然触发下午不算工时
    }

    // ── 复现最早的"午间打卡被误判成下班"bug：改判断依据为"离下班还有多久" ────────────
    [Fact]
    public void 离下班时间还早的打卡_不算下班候选()
    {
        var shift = DayShift(new TimeOnly(8, 30), new TimeOnly(20, 0));
        var noonPunch = WorkDate.ToDateTime(new TimeOnly(12, 5));   // 离 20:00 下班还有将近 8 小时
        Assert.False(AttendanceService.IsEligibleClockOutCandidate(noonPunch, WorkDate, shift));
    }

    [Fact]
    public void 离下班时间不到2小时的打卡_算下班候选()
    {
        var shift = DayShift(new TimeOnly(8, 30), new TimeOnly(20, 0));
        var eveningPunch = WorkDate.ToDateTime(new TimeOnly(19, 0));   // 离 20:00 下班只有 1 小时
        Assert.True(AttendanceService.IsEligibleClockOutCandidate(eveningPunch, WorkDate, shift));
    }

    [Fact]
    public void 没有排班信息时_按老办法直接当下班()
    {
        var punch = WorkDate.ToDateTime(new TimeOnly(9, 0));
        Assert.True(AttendanceService.IsEligibleClockOutCandidate(punch, WorkDate, null));
    }

    [Fact]
    public void 跨天夜班_凌晨打卡按第二天的应下班时间判断()
    {
        // 20:00 上班、次日 06:00 下班的跨天夜班；凌晨 05:30 打卡，离"第二天 06:00"不到 2 小时，应算下班候选
        var shift = DayShift(new TimeOnly(20, 0), new TimeOnly(6, 0), crossDay: true);
        var earlyMorningPunch = WorkDate.AddDays(1).ToDateTime(new TimeOnly(5, 30));
        Assert.True(AttendanceService.IsEligibleClockOutCandidate(earlyMorningPunch, WorkDate, shift));

        // 半夜 01:00 打卡，离第二天 06:00 下班还有 5 小时，不该被当成下班（应是午间/中途打卡）
        var midnightPunch = WorkDate.AddDays(1).ToDateTime(new TimeOnly(1, 0));
        Assert.False(AttendanceService.IsEligibleClockOutCandidate(midnightPunch, WorkDate, shift));
    }

    // ── 工时计算公式本身：半小时进位、6/9 小时两道扣时阈值 ──────────────────────────
    [Theory]
    [InlineData(8, 30, 17, 30, 8.0)]    // 9 小时在岗，超 6 小时扣午休（60），不超 9 小时不扣晚餐 → 8.0
    [InlineData(8, 0, 19, 0, 9.5)]      // 11 小时在岗，超 9 小时再扣晚餐（30） → 660-60-30=570min=9.5
    [InlineData(9, 0, 11, 0, 2.0)]      // 2 小时在岗，不超 6 小时，不扣任何休息时间
    public void 工时计算按阈值正确扣除午休晚餐(int inH, int inM, int outH, int outM, decimal expected)
    {
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(inH, inM));
        var clockOut = WorkDate.ToDateTime(new TimeOnly(outH, outM));
        Assert.Equal(expected, AttendanceService.ComputeWorkHours(clockIn, clockOut, lunchBreak: 60, dinnerBreak: 30));
    }

    [Fact]
    public void 下班早于上班时_工时不为负_直接算0()
    {
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(13, 0));
        var clockOut = WorkDate.ToDateTime(new TimeOnly(12, 0));   // 倒挂（比如漏打导致的异常数据）
        Assert.Equal(0m, AttendanceService.ComputeWorkHours(clockIn, clockOut, 60, 30));
    }

    [Theory]
    [InlineData(1.2, 1.0)]
    [InlineData(1.7, 1.5)]
    [InlineData(1.5, 1.5)]
    [InlineData(0.4, 0.0)]
    public void 半小时取整只舍不入(decimal input, decimal expected)
    {
        Assert.Equal(expected, AttendanceService.FloorToHalf(input));
    }

    [Fact]
    public void 提前打卡不多算工时_有效上班时间不早于排班时间()
    {
        var shift = DayShift(new TimeOnly(8, 30), new TimeOnly(20, 0));
        var earlyClockIn = WorkDate.ToDateTime(new TimeOnly(7, 0));   // 提前 1.5 小时到岗
        var effIn = AttendanceService.ClampEffectiveClockIn(WorkDate, earlyClockIn, shift, []);
        Assert.Equal(WorkDate.ToDateTime(new TimeOnly(8, 30)), effIn);
    }

    [Fact]
    public void 晚走不多算工时_有效下班时间不晚于排班时间()
    {
        var shift = DayShift(new TimeOnly(8, 30), new TimeOnly(20, 0));
        var lateClockOut = WorkDate.ToDateTime(new TimeOnly(22, 0));   // 晚走 2 小时
        var effOut = AttendanceService.ClampEffectiveClockOut(WorkDate, lateClockOut, shift);
        Assert.Equal(WorkDate.ToDateTime(new TimeOnly(20, 0)), effOut);
    }
}
