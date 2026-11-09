using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 迟到满 4 小时按半天旷工（2026-11-09 用户确认，从 11-09 起生效）：
/// 当天正班工时最多算半个标准工时（8 小时班 → 4 小时），出勤天数按 0.5 天算；更早的历史不回头改。
/// </summary>
public class HalfDayAbsentTests : SqliteTestBase
{
    private static readonly DateOnly RuleDate   = new(2026, 11, 9);   // 生效日
    private static readonly DateOnly BeforeDate = new(2026, 11, 6);   // 生效日之前

    private int SeedLateDay(DateOnly date, int lateMinutes)
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "半天旷工测试组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var shift = DayShift();                 // 08:30–17:30，标准工时 8
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        db.SaveChanges();

        var user = U("HDA1", "半天旷工测试");
        user.AttendanceGroupId = group.Id;
        user.HireDate = new DateOnly(2026, 1, 1);
        db.Users.Add(user);
        db.SaveChanges();

        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = date, ShiftScheduleId = shift.Id });
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            UserId = user.Id, WorkDate = date,
            ClockInTime  = date.ToDateTime(new TimeOnly(13, 0)),   // 13:00 才到（迟到 270 分钟）
            ClockOutTime = date.ToDateTime(new TimeOnly(17, 30)),
            AttendanceStatus = AttendanceStatus.Late,
            LateMinutes = lateMinutes,
            ActualWorkHours = 0
        });
        db.SaveChanges();
        return user.Id;
    }

    [Fact]
    public async Task 迟到满4小时_工时封顶半天_出勤算半天()
    {
        var uid = SeedLateDay(RuleDate, 270);
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);

        await svc.RecalcHoursAfterScheduleAsync([uid], RuleDate, RuleDate);
        await svc.GenerateMonthlySummaryAsync(RuleDate.Year, RuleDate.Month, [uid]);

        db.ChangeTracker.Clear();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == RuleDate);
        Assert.Equal(4m, rec.ActualWorkHours);        // 13:00–17:30 = 4.5h，封顶到半个标准工时 4h

        var sum = await db.MonthlyAttendanceSummaries
            .SingleAsync(s => s.UserId == uid && s.Year == RuleDate.Year && s.Month == RuleDate.Month);
        Assert.Equal(0.5m, sum.ActualWorkdays);       // 出勤只算半天
    }

    [Fact]
    public async Task 生效日之前_不套半天旷工()
    {
        var uid = SeedLateDay(BeforeDate, 270);
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);

        await svc.RecalcHoursAfterScheduleAsync([uid], BeforeDate, BeforeDate);

        db.ChangeTracker.Clear();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == BeforeDate);
        Assert.Equal(4.5m, rec.ActualWorkHours);      // 不封顶，历史数据不动
    }

    [Theory]
    [InlineData(239, true, false, false)]   // 差 1 分钟不算
    [InlineData(240, true, false, true)]    // 正好 4 小时 → 算
    [InlineData(240, false, false, false)]  // 休息日不算
    [InlineData(240, true, true, false)]    // 计时工不算
    public void 半天旷工判定边界(int lateMinutes, bool workday, bool hourly, bool expected)
    {
        var shift = DayShift();
        if (!workday) shift.RestDaysOfWeek = "0,1,2,3,4,5,6";   // 一周都算休息日
        var record = new AttendanceRecord
        {
            WorkDate = RuleDate, AttendanceStatus = AttendanceStatus.Late, LateMinutes = lateMinutes
        };
        Assert.Equal(expected, AttendanceService.IsHalfDayAbsentByLate(record, RuleDate, shift, hourly));
    }

    [Fact]
    public void 工时封顶值_是半个标准工时()
    {
        Assert.Equal(4m, AttendanceService.ApplyHalfDayAbsentCap(8m, DayShift(), 8m));    // 8 小时班 → 4
        Assert.Equal(4m, AttendanceService.ApplyHalfDayAbsentCap(4.5m, DayShift(), 8m));  // 超过半天 → 4
        Assert.Equal(3m, AttendanceService.ApplyHalfDayAbsentCap(3m, DayShift(), 8m));    // 不到半天 → 原样
        Assert.Equal(5.5m, AttendanceService.ApplyHalfDayAbsentCap(11m, NightShift(), 8m)); // 夜班标准 11 → 5.5
    }
    [Fact]
    public void 迟到4小时后状态改出差_出勤算整天_不再按半天旷工()
    {
        // 场景：当天确实迟到 4.5 小时（LateMinutes=270 残留），但状态后来被出差审批改成「出差」。
        // 出差按全勤算 1 天，不能被残留的迟到分钟数算成 0.5 天（2026-11-09 用户指出的统计错误）。
        var record = new AttendanceRecord
        {
            WorkDate = RuleDate,
            ClockInTime = RuleDate.ToDateTime(new TimeOnly(13, 0)),
            AttendanceStatus = AttendanceStatus.BusinessTrip,
            LateMinutes = 270
        };
        Assert.Equal(1m, AttendanceService.ResolveAttendanceDayCredit(record, 8m));
        Assert.False(AttendanceService.IsHalfDayAbsentByLate(record, RuleDate, DayShift(), hourly: false));
    }

    [Fact]
    public void 迟到4小时后状态改请假_按请假比例算_不按半天旷工()
    {
        // 全天请假（8 小时）当天出勤 = 0；半天假按 1−请假占比，与迟到分钟数无关。
        var fullDay = new AttendanceRecord
        {
            WorkDate = RuleDate,
            ClockInTime = RuleDate.ToDateTime(new TimeOnly(13, 0)),
            AttendanceStatus = AttendanceStatus.OnLeave,
            LeaveHours = 8m,
            LateMinutes = 270
        };
        Assert.Equal(0m, AttendanceService.ResolveAttendanceDayCredit(fullDay, 8m));

        var halfDay = new AttendanceRecord
        {
            WorkDate = RuleDate,
            ClockInTime = RuleDate.ToDateTime(new TimeOnly(13, 0)),
            AttendanceStatus = AttendanceStatus.OnLeave,
            LeaveHours = 2m,      // 2/8 = 0.25 → 请假 0.5 天，出勤 0.5 天
            LateMinutes = 270
        };
        Assert.Equal(0.5m, AttendanceService.ResolveAttendanceDayCredit(halfDay, 8m));
    }
}