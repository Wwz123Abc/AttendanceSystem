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
        var hours = AttendanceService.ComputeWorkHours(effIn, effOut);
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

    // ── 工时计算公式本身：半小时进位、压中公司统一的固定饭点时段就扣对应重叠 ──────────────
    [Theory]
    [InlineData(8, 30, 17, 30, 8.0)]    // 9 小时在岗，压中午间 12:00-13:00 整段 → 扣 60 分钟 → 8.0
    [InlineData(8, 0, 19, 0, 9.5)]      // 11 小时在岗，压中午间+晚餐两段 → 扣 90 分钟 → 660-90=570min=9.5
    [InlineData(9, 0, 11, 0, 2.0)]      // 2 小时在岗，不挨着任何固定时段，不扣
    [InlineData(8, 30, 17, 23, 7.5)]    // 2026-09-17 口径统一：出口按半小时取整，不再是 2 位小数——
                                         // 8:53 在岗压中午间扣 60 分钟=473min=7.8833h，向下取整到 7.5
    public void 工时计算按固定饭点时段正确扣除重叠部分(int inH, int inM, int outH, int outM, decimal expected)
    {
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(inH, inM));
        var clockOut = WorkDate.ToDateTime(new TimeOnly(outH, outM));
        Assert.Equal(expected, AttendanceService.ComputeWorkHours(clockIn, clockOut));
    }

    // ── 2026-09-29 用户确认：取消"超过 6/9 小时才扣整段"的时长门槛，改成按实际重叠时长精确扣减，
    // 不会再出现"多干一分钟反而少算近一小时"的悬崖 ────────────────────────────────────

    [Fact]
    public void 恰好6小时且压中午间_按新规则照样扣_不再是老规则的不扣()
    {
        // 9:00-15:00 正好 6 小时，完整压中 12:00-13:00：老规则"未超过 6 小时不扣"会算出 6 小时，
        // 新规则只看有没有重叠，一律扣掉重叠的 60 分钟，变成 5 小时
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(9, 0));
        var clockOut = WorkDate.ToDateTime(new TimeOnly(15, 0));
        Assert.Equal(5.0m, AttendanceService.ComputeWorkHours(clockIn, clockOut));
    }

    [Fact]
    public void 没有悬崖_多打卡一分钟工时不会反而变少()
    {
        // 老规则的"悬崖"：6h00m 不扣、6h01m 突然扣掉整整 60 分钟，反而比工作时间短的人算得还少。
        // 新规则按实际重叠算，工时应该随在岗时长单调不减——多干一分钟，工时最多持平，不会变少。
        var clockIn = WorkDate.ToDateTime(new TimeOnly(9, 0));
        var h1 = AttendanceService.ComputeWorkHours(clockIn, WorkDate.ToDateTime(new TimeOnly(15, 0)));   // 6h00m
        var h2 = AttendanceService.ComputeWorkHours(clockIn, WorkDate.ToDateTime(new TimeOnly(15, 1)));   // 6h01m
        Assert.True(h2 >= h1);
    }

    [Fact]
    public void 只工作了半个午休_只扣重叠的那一半_不是整段60分钟()
    {
        // 11:45-12:15：只跟午间时段（12:00-13:00）重叠 15 分钟，原始 30 分钟只扣 15 分钟，剩 15 分钟=0.25h，
        // 按半小时取整舍去变成 0——验证的是"只扣实际重叠"而不是"沾到点边就扣整段 60 分钟"
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(11, 45));
        var clockOut = WorkDate.ToDateTime(new TimeOnly(12, 15));
        Assert.Equal(0m, AttendanceService.ComputeWorkHours(clockIn, clockOut));
    }

    [Fact]
    public void 全程都在午间时段里_工时算0()
    {
        // 12:10-12:40 整段都在 12:00-13:00 里面，压根没在"上班"，工时应该是 0
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(12, 10));
        var clockOut = WorkDate.ToDateTime(new TimeOnly(12, 40));
        Assert.Equal(0m, AttendanceService.ComputeWorkHours(clockIn, clockOut));
    }

    [Theory]
    [InlineData(17, 0, 18, 30, 1.0)]     // 压中晚餐 17:30-18:00（30分钟）：1.5h-0.5h=1.0h
    [InlineData(23, 30, 0, 0, 0.5)]      // 23:30 到次日 00:00：正好在 00:00-01:00 开始前结束，没有重叠，半小时原样不扣
    [InlineData(5, 0, 6, 30, 1.0)]       // 压中早餐 05:30-06:00：1.5h-0.5h=1.0h
    public void 晚餐和早餐时段也按同样规则扣(int inH, int inM, int outH, int outM, decimal expected)
    {
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(inH, inM));
        var clockOut = outH == 0 && outM == 0 ? WorkDate.AddDays(1).ToDateTime(TimeOnly.MinValue) : WorkDate.ToDateTime(new TimeOnly(outH, outM));
        Assert.Equal(expected, AttendanceService.ComputeWorkHours(clockIn, clockOut));
    }

    [Fact]
    public void 跨天夜班_同时压中宵夜和早餐两段()
    {
        // 20:00 上班到次日 08:00 下班：跨过 00:00-01:00（宵夜）和 05:30-06:00（早餐）两段，各扣一次
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(20, 0));
        var clockOut = WorkDate.AddDays(1).ToDateTime(new TimeOnly(8, 0));
        // 原始 12 小时，扣 1 小时宵夜 + 0.5 小时早餐 = 10.5 小时
        Assert.Equal(10.5m, AttendanceService.ComputeWorkHours(clockIn, clockOut));
    }

    [Fact]
    public void 工时结果对再次取半小时是幂等的()
    {
        // §5.2 验收 2：已经按半小时取整的值，再取一次不应该变——这是"月合计不变"这条验收标准的
        // 数学基础（月合计本来就是"逐日 FloorToHalf 后累加"，日值现在也按这个口径取整，再取一次要不变）
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(8, 30));
        var clockOut = WorkDate.ToDateTime(new TimeOnly(17, 30));
        var once  = AttendanceService.ComputeWorkHours(clockIn, clockOut);
        var twice = AttendanceService.FloorToHalf(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void 下班早于上班时_工时不为负_直接算0()
    {
        var clockIn  = WorkDate.ToDateTime(new TimeOnly(13, 0));
        var clockOut = WorkDate.ToDateTime(new TimeOnly(12, 0));   // 倒挂（比如漏打导致的异常数据）
        Assert.Equal(0m, AttendanceService.ComputeWorkHours(clockIn, clockOut));
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

    // ── 复现 "GA 员工白班/夜班配置重复窗口导致整天工时清零" 的真实场景（2026-09-18）───────
    [Fact]
    public void 配置里重复的午间窗口会被去重()
    {
        // 同一段时间窗口手滑配了两次——去重前会被"取最晚一段""排除最后一段"这些按下标区分窗口的
        // 逻辑误当成两段独立窗口，导致上班时间被顺延到窗口结束、下班时间又被同一窗口收窄到窗口开始，
        // 两边一起收缩到同一个点，整天工时变成 0（489 名 GA 员工受影响的真实生产 bug）。
        var shift = new ShiftSchedule { MidCheckWindows = "12:00-13:00,12:00-13:00" };
        var windows = shift.ParseMidCheckWindows();
        Assert.Single(windows);
        Assert.Equal((new TimeOnly(12, 0), new TimeOnly(13, 0)), windows[0]);
    }

    [Fact]
    public void 冻结在记录里的重复窗口字符串也会被去重()
    {
        // ParseMidCheckWindows 管的是"实时读班次配置"这条路；已经写进历史记录、冻结下来的
        // MidCheckResults 字符串走的是另一个解析方法（ParseMidCheckResults），两边都要去重，
        // 不然月度报表"自愈"重算老记录时，读到的还是冻结下来的重复窗口字符串，一样会清零工时。
        var results = "12:00-13:00=;12:00-13:00=".ParseMidCheckResults();
        Assert.Single(results);
        Assert.False(results[0].IsSatisfied);
    }
}
