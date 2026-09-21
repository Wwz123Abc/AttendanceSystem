using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
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
    [InlineData(6.5, 8, 1)]    // 占比 81.25%，>=0.75 → 算 1 天
    [InlineData(5, 8, 0.5)]    // 占比 62.5%，就近取整到 0.5 天（2026-09-21 起不再是 >0.5 就算整天）
    [InlineData(3.5, 8, 0.5)]  // 占比 43.75%，落在 [0.25,0.75) → 算半天，不是按比例给 0.4375 天
    [InlineData(1, 8, 0)]      // 占比 12.5%，<0.25 → 算 0 天
    [InlineData(0, 8, 0)]      // 没有请假小时数 → 0 天
    public void 请假天数按占比折算_不是按状态是否请假就算一整天(decimal leaveHours, decimal standardHours, decimal expectedDays)
    {
        Assert.Equal(expectedDays, AttendanceService.ResolveLeaveDaysFraction(leaveHours, standardHours));
    }

    [Fact]
    public void 边界情况_恰好占比50百分之_算半天_不会被顶成整天()
    {
        // 请假 4 小时、标准工时 8 小时——最常见的"标准半天假"场景，占比恰好 50%，落在
        // [0.25, 0.75) 区间内，就近取整到 0.5 天，不是 1 整天。
        Assert.Equal(0.5m, AttendanceService.ResolveLeaveDaysFraction(4m, 8m));
    }

    [Fact]
    public void 上午半天假和下午半天假折算的天数现在一样_不再因为占比卡在0点5两侧而差一倍()
    {
        // 标准白班 08:30-12:00 + 13:00-17:30、标准工时 8 小时：上午假 3.5 小时（占比 43.75%）、
        // 下午假 4.5 小时（占比 56.25%）——旧口径下前者算 0.5 天、后者算 1 整天，同样是"半天假"
        // 天数差一倍；改成就近取整到 0.5 后两者都落在 [0.25,0.75) 区间，一致算 0.5 天（2026-09-21）。
        Assert.Equal(0.5m, AttendanceService.ResolveLeaveDaysFraction(3.5m, 8m));
        Assert.Equal(0.5m, AttendanceService.ResolveLeaveDaysFraction(4.5m, 8m));
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

    [Fact]
    public void 短时长请假不足半小时被取整成0小时_但这一天仍算有真实交集()
    {
        // 请假 20 分钟——ComputeLeaveHoursForDay 会因为 FloorToHalf 取整成 0 小时，
        // 但 HasLeaveOverlapForDay 要能识别出这一天确实有交集，不能被当成"没交集"直接跳过
        // （发现于 2026-09-18：09-18 那次"无交集跳过"修复，把这种情况和真没交集混为一谈）。
        var day   = new DateOnly(2026, 9, 10);
        var start = new DateTime(2026, 9, 10, 9, 0, 0);
        var end   = new DateTime(2026, 9, 10, 9, 20, 0);

        var hours = AttendanceService.ComputeLeaveHoursForDay(day, start, end, Lunch, Dinner, StandardHours);
        Assert.Equal(0m, hours);   // 取整后确实是 0

        Assert.True(AttendanceService.HasLeaveOverlapForDay(day, start, end));   // 但这一天不该被跳过
    }

    [Fact]
    public void 请假结束时间恰好卡在午夜_区间最后一天真的没有交集()
    {
        // 请假到 9-11 00:00——区间最后一天（9-11）跟请假时段完全没有交集，
        // 这种天才应该被跳过，不新建记录、不标"请假"状态。
        var lastDay = new DateOnly(2026, 9, 11);
        var start   = new DateTime(2026, 9, 10, 20, 0, 0);
        var end     = new DateTime(2026, 9, 11, 0, 0, 0);

        Assert.False(AttendanceService.HasLeaveOverlapForDay(lastDay, start, end));
    }

    // ── 覆盖 ResolveAttendanceDayCredit：出勤天数 + 请假天数必须恰好加起来是 1 天，不能重复计满 ──
    // GenerateMonthlySummaryAsync 和 GenerateTemplateReportAsync（模板汇总表/发工资用的那份导出）
    // 共用这一个方法，2026-09-21 发现两处以前各算各的，同一个人同一个月两份报表出勤天数对不上。
    [Fact]
    public void 半天假当天出勤算0点5天_跟请假天数加起来正好1天()
    {
        var record = new AttendanceRecord
        {
            ClockInTime      = new DateTime(2026, 9, 10, 8, 30, 0),
            AttendanceStatus = AttendanceStatus.OnLeave,
            LeaveHours       = 4m   // 标准工时 8 小时的一半
        };
        var dayCredit = AttendanceService.ResolveAttendanceDayCredit(record, StandardHours);
        Assert.Equal(0.5m, dayCredit);
        Assert.Equal(1m, dayCredit + AttendanceService.ResolveLeaveDaysFraction(record.LeaveHours, StandardHours));
    }

    [Fact]
    public void 整天请假没打卡_出勤算0天_全部记到请假天数()
    {
        var record = new AttendanceRecord
        {
            ClockInTime      = null,
            AttendanceStatus = AttendanceStatus.OnLeave,
            LeaveHours       = 8m
        };
        Assert.Equal(0m, AttendanceService.ResolveAttendanceDayCredit(record, StandardHours));
    }

    [Fact]
    public void 正常出勤且不请假_算满1天()
    {
        var record = new AttendanceRecord
        {
            ClockInTime      = new DateTime(2026, 9, 10, 8, 30, 0),
            AttendanceStatus = AttendanceStatus.Normal
        };
        Assert.Equal(1m, AttendanceService.ResolveAttendanceDayCredit(record, StandardHours));
    }

    [Fact]
    public void 旷工没打卡_出勤算0天()
    {
        var record = new AttendanceRecord
        {
            ClockInTime      = null,
            AttendanceStatus = AttendanceStatus.Absent
        };
        Assert.Equal(0m, AttendanceService.ResolveAttendanceDayCredit(record, StandardHours));
    }
}
