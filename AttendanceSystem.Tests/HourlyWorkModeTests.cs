using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 计时工模式（考勤组 HourlyWorkStartDate，2026-10-20 起用于成都鹰诺深圳分公司临时工）：
/// 工时 = 最后一次下班卡 − 第一次上班卡，扣午饭晚饭，不封顶；没有加班/旷工/迟到早退；午间漏打只算窗口开始之前；
/// 00:00–07:00 的打卡无效；只对开启了的考勤组生效，别的组完全不变。
/// </summary>
public class HourlyWorkModeTests : SqliteTestBase
{
    private static DateTime At(int h, int m) => Tue.ToDateTime(new TimeOnly(h, m));

    private async Task SyncAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await sync.ProcessAttLogAsync("SNM", times.Select(t => new ZKAttLogRow("M1", t, 0, 15)).ToList());
    }

    private (int uid, int groupId) SeedHourly(bool hourlyOn = true)
    {
        var (uid, _) = SeedDayMidWorld();   // 白班、午间窗口 12:00–13:00、考勤机 SNM、工号 M1
        using var db = CreateContext();
        var group = db.AttendanceGroups.Single(g => g.Id == db.Users.Single(u => u.Id == uid).AttendanceGroupId);
        if (hourlyOn) group.HourlyWorkStartDate = Tue;
        db.SaveChanges();
        return (uid, group.Id);
    }

    private async Task<AttendanceRecord> TueRecordAsync(int uid)
    {
        using var db = CreateContext();
        return await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
    }

    [Fact]
    public async Task 午间打上_取最晚一次下班卡_工时不封顶()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20), At(18, 10), At(21, 0));
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(21, 0), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 11m, 11.1m);   // 08:25–21:00 = 12 小时 35 分，扣午饭 1 小时、晚饭 0.5 小时
        Assert.Equal(AttendanceStatus.Normal, rec.AttendanceStatus);
    }

    [Fact]
    public async Task 午间窗口漏打_只算窗口开始之前的工时()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(21, 0));
        var rec = await TueRecordAsync(uid);
        Assert.InRange(rec.ActualWorkHours, 3.5m, 3.6m);   // 08:25–12:00，下午不算
    }

    [Fact]
    public async Task 凌晨0点到7点的打卡无效_不会变成第二天的上班卡()
    {
        var (uid, _) = SeedHourly();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(8, 25), At(12, 20), At(17, 40), wed.ToDateTime(new TimeOnly(0, 30)), wed.ToDateTime(new TimeOnly(6, 59)));
        using var db = CreateContext();
        Assert.False(await db.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchTime >= wed.ToDateTime(TimeOnly.MinValue) && p.PunchTime < wed.ToDateTime(new TimeOnly(7, 0))));
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == wed));
        Assert.Equal(At(17, 40), (await TueRecordAsync(uid)).ClockOutTime);   // 凌晨那两次没有被当成下班卡
    }

    [Fact]
    public async Task 当天23点59分的下班卡有效()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20), At(23, 59));
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(23, 59), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 14m, 14.1m);
    }

    [Fact]
    public async Task 计时工上班卡照常判断迟到_但没有早退_工时不受迟到影响()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(10, 0), At(12, 20), At(18, 0));   // 班次 08:30 上班，10:00 才到
        var rec = await TueRecordAsync(uid);
        Assert.Equal(AttendanceStatus.Late, rec.AttendanceStatus);
        Assert.True(rec.LateMinutes > 0);
        Assert.Equal(0, rec.EarlyLeaveMinutes);
        Assert.Equal(6.5m, rec.ActualWorkHours);   // 10:00–18:00 = 8 小时，扣午饭 1 小时、晚饭 0.5 小时（17:30–18:00）；迟到只记录，工时按实际到岗时间算
    }

    [Fact]
    public async Task 只有上班卡和午间卡_最晚一次打卡当下班_午间已打上不扣下午()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20));
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(12, 20), rec.ClockOutTime);                // 最晚一次打卡就是下班时间
        Assert.InRange(rec.ActualWorkHours, 3.5m, 3.6m);           // 08:25–12:20 = 3 小时 55 分，扣午饭重叠的 20 分钟
        Assert.Equal(AttendanceStatus.Normal, rec.AttendanceStatus);
    }

    [Fact]
    public async Task 只有上班卡_没有任何其他打卡_工时为0()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25));
        var rec = await TueRecordAsync(uid);
        Assert.Null(rec.ClockOutTime);
        Assert.Equal(0m, rec.ActualWorkHours);
    }

    [Fact]
    public async Task 没开计时工模式的考勤组_工时仍按班次封顶_完全不变()
    {
        var (uid, _) = SeedHourly(hourlyOn: false);
        await SyncAsync(At(8, 25), At(12, 20), At(18, 10), At(21, 0));
        var rec = await TueRecordAsync(uid);
        Assert.True(rec.ActualWorkHours <= 8.5m, $"普通考勤组工时应按班次封顶，实际 {rec.ActualWorkHours}");
    }

    [Fact]
    public async Task 计时工的发薪日格子不封顶_普通组仍封顶标准工时()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20), At(18, 10), At(21, 0));
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var row = await svc.GetMyPayrollRowAsync(uid, Tue, Tue);
        Assert.Equal(11m, row!.DailyHours[0]);   // 11.08 向下取整到 0.5，不是被封顶的 8
    }

    [Fact]
    public async Task 计时工提交加班申请被拦住()
    {
        var (uid, _) = SeedHourly();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Overtime, Reason = "t",
            OvertimeStartTime = At(18, 0), OvertimeEndTime = At(21, 0)
        }));
        Assert.Contains("计时工", ex.Message);
    }

    [Fact]
    public void 计时工的打卡类型_第一次上班_窗口内午间_其余下班()
    {
        var shift = DayShift(); shift.MidCheckWindows = "12:00-13:00";
        Assert.Equal(PunchType.ClockIn,  AttendanceService.ResolveHourlyPunchType(At(8, 20),  Tue, shift, false));
        Assert.Equal(PunchType.MidCheck, AttendanceService.ResolveHourlyPunchType(At(12, 30), Tue, shift, true));
        Assert.Equal(PunchType.MidCheck, AttendanceService.ResolveHourlyPunchType(At(13, 20), Tue, shift, true));   // 窗口结束后 30 分钟宽限内
        Assert.Equal(PunchType.ClockOut, AttendanceService.ResolveHourlyPunchType(At(13, 45), Tue, shift, true));
        Assert.Equal(PunchType.ClockOut, AttendanceService.ResolveHourlyPunchType(At(18, 0),  Tue, shift, true));   // 下班时间不固定，傍晚的卡也是下班
        Assert.Equal(PunchType.ClockOut, AttendanceService.ResolveHourlyPunchType(At(10, 0),  Tue, shift, true));   // 上午随手打的卡暂记下班，被之后更晚的覆盖
    }

    private async Task<PunchResponseDto> PunchAtAsync(int uid, DateTime when, PunchType type)
    {
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance, new FixedClock(when));
        return await svc.PunchAsync(uid, new PunchRequestDto { PunchType = type }, skipLocationCheck: true);
    }

    [Fact]
    public async Task 手机远程打卡_凌晨无效_白天正常_多次下班卡取最晚()
    {
        var (uid, _) = SeedHourly();
        var wed = Tue.AddDays(1);
        var night = await PunchAtAsync(uid, wed.ToDateTime(new TimeOnly(2, 30)), PunchType.ClockIn);
        Assert.False(night.Success);
        Assert.Contains("07:00", night.Message);

        Assert.True((await PunchAtAsync(uid, At(8, 20), PunchType.ClockIn)).Success);
        Assert.True((await PunchAtAsync(uid, At(12, 40), PunchType.MidCheck)).Success);
        Assert.True((await PunchAtAsync(uid, At(17, 0), PunchType.ClockOut)).Success);
        Assert.True((await PunchAtAsync(uid, At(20, 30), PunchType.ClockOut)).Success);   // 又打了一次，取更晚的
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(20, 30), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 10.5m, 10.6m);   // 08:20–20:30 = 12h10m − 1h − 0.5h
        Assert.Equal(AttendanceStatus.Normal, rec.AttendanceStatus);
        using var db = CreateContext();
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == wed));   // 凌晨那次没有生成第二天的记录
    }

    private AttendanceService Svc(AttendanceDbContext db) => new(db, AppOptions, NullLogger<AttendanceService>.Instance);

    [Fact]
    public async Task 只有上班卡和午间卡后补下班卡_补的时间比午间卡晚才有效_工时重新算到补的下班时间()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20));   // 下班时间暂时记成最晚一次打卡 12:20
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(18, 0), false, null, "管理员");
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(18, 0), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 7.5m, 8m);   // 08:25–18:00 = 9 小时 35 分，扣午饭 1 小时、晚饭 0.5 小时 = 8.08，系统存的是向下取整前的值
        using var db2 = CreateContext();
        // 补的时间比现有"下班时间"（12:20）还早：不生效，仍按最晚的算
        await Assert.ThrowsAsync<BusinessException>(() =>
            Svc(db2).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(11, 0), false, null, "管理员"));
    }

    [Fact]
    public async Task 计时工补卡不能补到第二天凌晨()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20));
        using var db = CreateContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, Tue.AddDays(1).ToDateTime(new TimeOnly(0, 30)), false, null, "管理员"));
        Assert.Contains("23:59", ex.Message);
    }
}
