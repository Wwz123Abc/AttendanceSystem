using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>饭点按班次设置（2026-10-26）：优先级 班次 → 考勤组 → 默认；加班时长按加班开始那天班次的设置算。</summary>
[Collection("UnpaidBreakStatic")]
public class ShiftMealBreakTests : SqliteTestBase, IDisposable
{
    public new void Dispose()
    {
        UnpaidBreakSettings.ResetAll();
        base.Dispose();
    }

    private static TimeOnly H(int h, int m = 0) => new(h, m);

    [Fact]
    public void 优先级_班次设了用班次的_没设用考勤组的_都没设用默认()
    {
        UnpaidBreakSettings.Apply(3, [(H(12), H(13)), (H(17), H(18))]);               // 考勤组 3
        UnpaidBreakSettings.ApplyShift(30, [(H(12), H(13)), (H(17, 30), H(18, 30))]);   // 班次 30（属于考勤组 3）

        Assert.Equal("12:00-13:00,17:30-18:30", UnpaidBreakSettings.Format(UnpaidBreakSettings.WindowsFor(3, 30)));
        Assert.Equal("12:00-13:00,17:00-18:00", UnpaidBreakSettings.Format(UnpaidBreakSettings.WindowsFor(3, 31)));   // 班次 31 没设：跟考勤组
        Assert.Equal("12:00-13:00,17:00-18:00", UnpaidBreakSettings.Format(UnpaidBreakSettings.WindowsFor(3)));
        Assert.Equal("00:00-01:00,05:30-06:00,12:00-13:00,17:30-18:00", UnpaidBreakSettings.Format(UnpaidBreakSettings.WindowsFor(4, 31)));   // 别的考勤组：默认
        Assert.True(UnpaidBreakSettings.IsShiftCustomized(30));
        Assert.False(UnpaidBreakSettings.IsShiftCustomized(31));

        UnpaidBreakSettings.RemoveShift(30);
        Assert.Equal("12:00-13:00,17:00-18:00", UnpaidBreakSettings.Format(UnpaidBreakSettings.WindowsFor(3, 30)));   // 不再单独设置：回到考勤组的
    }

    [Fact]
    public void 班次可以设成没有饭点_加班按起止时间全额算()
    {
        UnpaidBreakSettings.ApplyShift(30, []);
        Assert.Empty(UnpaidBreakSettings.WindowsFor(3, 30));
        Assert.Equal(9m, AttendanceService.ComputeWorkHours(new DateTime(2026, 10, 5, 8, 30, 0), new DateTime(2026, 10, 5, 17, 30, 0), 3, 30));
    }

    [Fact]
    public async Task 程序启动时从库里读出班次和考勤组的设置()
    {
        using (var db = CreateContext())
        {
            db.SystemSettings.AddRange(
                new SystemSetting { Key = "UnpaidBreakWindows:3", Value = "12:00-13:00,17:00-18:00" },
                new SystemSetting { Key = "UnpaidBreakWindows:shift:30", Value = "12:00-13:00,17:30-18:30" },
                new SystemSetting { Key = "UnpaidBreakWindows:shift:abc", Value = "12:00-13:00" },        // 编号读不懂：跳过
                new SystemSetting { Key = "UnpaidBreakWindows:shift:31", Value = "12:00-11:00" });        // 校验不过：跳过
            await db.SaveChangesAsync();
        }
        using (var db = CreateContext()) await UnpaidBreakSettings.LoadAsync(db);

        Assert.Equal("12:00-13:00,17:30-18:30", UnpaidBreakSettings.Format(UnpaidBreakSettings.WindowsFor(3, 30)));
        Assert.Equal("12:00-13:00,17:00-18:00", UnpaidBreakSettings.Format(UnpaidBreakSettings.WindowsFor(3, 31)));
        Assert.False(UnpaidBreakSettings.IsShiftCustomized(31));
    }

    [Fact]
    public void 休息日白班加班_按加班那天班次的饭点算_班次设了17点半晚饭就不多扣()
    {
        var sun = new DateOnly(2026, 9, 6);
        var white = DayShift(); white.Id = 30; white.ShiftName = "白班";
        var otherShift = DayShift(); otherShift.Id = 31; otherShift.ShiftName = "早白班";
        UnpaidBreakSettings.Apply(3, [(H(12), H(13)), (H(17), H(18))]);                  // 考勤组晚饭 17:00-18:00
        UnpaidBreakSettings.ApplyShift(30, [(H(12), H(13)), (H(17, 30), H(18, 30))]);    // 白班晚饭 17:30-18:30
        var start = sun.ToDateTime(H(8, 30));
        var end = sun.ToDateTime(H(17, 30));

        Assert.Equal(8m, AttendanceService.ComputeOvertimeHoursOutsideShift(start, end, [(sun, white)], 3));        // 白班：只扣午饭
        Assert.Equal(7.5m, AttendanceService.ComputeOvertimeHoursOutsideShift(start, end, [(sun, otherShift)], 3));  // 没单独设置的班次：跟考勤组，晚饭多扣 0.5
    }
}
