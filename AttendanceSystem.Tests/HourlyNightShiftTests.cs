using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 计时工模式下的夜班（2026-10-21，售后考勤组加夜班班次）：排的是跨天班次的人，上班日 19:00 到次日 10:00 的打卡有效，
/// 次日早上的下班卡归上班那天；工时 = 最后一次打卡 − 第一次打卡，只扣次日 00:00–01:00 的饭，不封顶，次日 10:00 之后不算；
/// 白班计时工的规则不变。
/// </summary>
public class HourlyNightShiftTests : SqliteTestBase
{
    private static DateTime Tue_(int h, int m) => Tue.ToDateTime(new TimeOnly(h, m));
    private static DateTime Wed_(int h, int m) => Tue.AddDays(1).ToDateTime(new TimeOnly(h, m));

    private int SeedNightHourly(string? midWindow = null)
    {
        var (uid, _) = SeedNightWorld();   // 周一、周二、周三都排夜班 20:00–次日 08:00，考勤机 SNN、工号 N1
        using var db = CreateContext();
        var gid = db.Users.Single(u => u.Id == uid).AttendanceGroupId;
        db.AttendanceGroups.Single(g => g.Id == gid).HourlyWorkStartDate = Mon;
        if (midWindow is not null)
            foreach (var s in db.ShiftSchedules.Where(s => s.AttendanceGroupId == gid)) s.MidCheckWindows = midWindow;
        db.AttendanceRecords.RemoveRange(db.AttendanceRecords.Where(r => r.UserId == uid));   // 去掉种子里周一那条没下班卡的记录，免得干扰
        db.SaveChanges();
        return uid;
    }

    private async Task SyncAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await sync.ProcessAttLogAsync("SNN", times.Select(t => new ZKAttLogRow("N1", t, 0, 15)).ToList());
    }

    [Fact]
    public async Task 夜班次日早上的下班卡归上班那天_工时只扣0点到1点的饭()
    {
        var uid = SeedNightHourly();
        await SyncAsync(Tue_(19, 55), Wed_(6, 30));
        using var db = CreateContext();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Tue_(19, 55), rec.ClockInTime);
        Assert.Equal(Wed_(6, 30), rec.ClockOutTime);
        Assert.Equal(9.5m, rec.ActualWorkHours);   // 19:55–06:30 = 10 小时 35 分，扣 00:00–01:00 的 1 小时 = 9.58 → 9.5（05:30–06:00 不扣）
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue.AddDays(1) && r.ClockInTime == Wed_(6, 30)));   // 次日早上的卡没有变成第二天的上班卡
    }

    [Fact]
    public async Task 夜班下班卡最晚算到次日10点_之后的打卡无效()
    {
        var uid = SeedNightHourly();
        await SyncAsync(Tue_(19, 30), Wed_(9, 50), Wed_(10, 30));   // 10:30 在有效范围外，不能当下班卡
        using var db = CreateContext();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Wed_(9, 50), rec.ClockOutTime);
        Assert.Equal(13m, rec.ActualWorkHours);   // 19:30–09:50 = 14 小时 20 分，扣 1 小时 = 13.33 → 13.0
    }

    [Fact]
    public async Task 夜班员工白天19点前的打卡无效()
    {
        var uid = SeedNightHourly();
        await SyncAsync(Tue_(15, 0));   // 周二排夜班，15:00 不在有效打卡时间里
        using var db = CreateContext();
        Assert.False(await db.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchTime == Tue_(15, 0)));
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
    }

    [Fact]
    public async Task 夜班上班卡晚于应上班时间照常记迟到_不影响工时()
    {
        var uid = SeedNightHourly();
        await SyncAsync(Tue_(20, 10), Wed_(8, 0));
        using var db = CreateContext();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(AttendanceStatus.Late, rec.AttendanceStatus);
        Assert.Equal(10.5m, rec.ActualWorkHours);   // 20:10–08:00 = 11 小时 50 分，扣 1 小时 = 10.83 → 10.5
    }

    [Fact]
    public void 夜班工时计算_不封顶_不扣白班饭点_10点后不算()
    {
        var night = NightShift();
        // 20:00 → 次日 10:00 = 14 小时，扣 00:00–01:00 一小时 = 13 小时
        Assert.Equal(13m, AttendanceService.ComputeHourlyWorkHours(Tue, Tue_(20, 0), Wed_(10, 0), night, null));
        // 下班卡晚于 10:00 的按 10:00 算
        Assert.Equal(13m, AttendanceService.ComputeHourlyWorkHours(Tue, Tue_(20, 0), Wed_(12, 0), night, null));
        // 20:00 → 23:30：没碰到 00:00–01:00，不扣
        Assert.Equal(3.5m, AttendanceService.ComputeHourlyWorkHours(Tue, Tue_(20, 0), Tue_(23, 30), night, null));
    }

    [Fact]
    public void 计时工打卡归属哪天_夜班和白班规则()
    {
        var night = NightShift();
        var day = DayShift();
        // 夜班：上班日 19:00 起有效，15:00 无效；次日 10:00 之前归昨天，之后（昨天排夜班、今天没班）按白班规则从 07:00 起算今天
        Assert.Equal(Tue, AttendanceService.ResolveHourlyWorkDate(Tue_(19, 0), night, null));
        Assert.Null(AttendanceService.ResolveHourlyWorkDate(Tue_(15, 0), night, null));
        Assert.Equal(Tue, AttendanceService.ResolveHourlyWorkDate(Wed_(9, 59), night, night));
        // 白班：07:00 起有效，凌晨无效
        Assert.Equal(Tue, AttendanceService.ResolveHourlyWorkDate(Tue_(7, 0), day, null));
        Assert.Null(AttendanceService.ResolveHourlyWorkDate(Tue_(3, 0), day, day));
        Assert.Null(AttendanceService.ResolveHourlyWorkDate(Tue_(3, 0), null, null));
    }

    private async Task<AttendanceSystem.Models.DTOs.PunchResponseDto> PunchAtAsync(int uid, DateTime when, PunchType type)
    {
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance, new FixedClock(when));
        return await svc.PunchAsync(uid, new AttendanceSystem.Models.DTOs.PunchRequestDto { PunchType = type }, skipLocationCheck: true);
    }

    [Fact]
    public async Task 手机远程打卡_夜班次日早上的下班卡归上班那天_白天19点前无效()
    {
        var uid = SeedNightHourly();
        Assert.True((await PunchAtAsync(uid, Tue_(19, 50), PunchType.ClockIn)).Success);
        var bad = await PunchAtAsync(uid, Wed_(10, 30), PunchType.ClockOut);   // 周三也排夜班，10:30 不在有效范围
        Assert.False(bad.Success);
        Assert.Contains("19:00", bad.Message);
        Assert.True((await PunchAtAsync(uid, Wed_(6, 40), PunchType.ClockOut)).Success);

        using var db = CreateContext();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Wed_(6, 40), rec.ClockOutTime);
        Assert.Equal(9.5m, rec.ActualWorkHours);   // 19:50–06:40 = 10 小时 50 分，扣 1 小时 = 9.83 → 9.5
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue.AddDays(1) && r.ClockInTime != null));
    }

    [Fact]
    public async Task 夜班补卡时间必须在上班日19点到次日10点之间()
    {
        var night = NightShift();
        var ctx = new ReplenishmentContext(night, null, null, null, null, [], Hourly: true);
        Assert.NotNull(PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, Tue_(15, 0), ctx));
        Assert.NotNull(PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, Wed_(10, 0), ctx));
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, Tue_(19, 0), ctx));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 夜班配了午间窗口_打上了正常算_只扣0点到1点的饭()
    {
        var uid = SeedNightHourly("00:00-01:00");
        await SyncAsync(Tue_(19, 55), Wed_(0, 20), Wed_(6, 30));   // 00:20 落在午间窗口里
        using var db = CreateContext();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Wed_(6, 30), rec.ClockOutTime);
        Assert.Equal(9.5m, rec.ActualWorkHours);   // 19:55–06:30 扣 1 小时 = 9.58 → 9.5
    }

    [Fact]
    public async Task 夜班午间窗口漏打_只算窗口开始之前的工时()
    {
        var uid = SeedNightHourly("00:00-01:00");
        await SyncAsync(Tue_(19, 55), Wed_(6, 30));   // 没在 00:00–01:30 之间打午间卡
        using var db = CreateContext();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(4m, rec.ActualWorkHours);   // 19:55–00:00 = 4 小时 5 分，后半段不算 → 4.0（这段不碰饭点，不扣）
    }

    [Fact]
    public async Task 夜班午间窗口宽限30分钟_0点40打的午间卡仍算打上()
    {
        var uid = SeedNightHourly("00:00-01:00");
        await SyncAsync(Tue_(19, 55), Wed_(1, 20), Wed_(6, 30));   // 01:20 在窗口结束后 30 分钟宽限内
        using var db = CreateContext();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(9.5m, rec.ActualWorkHours);
    }
}
