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

/// <summary>管理员手动补卡的"撤回"：恢复成补卡前的样子；只能撤回最近一次、没被别的操作改过的补卡。</summary>
public class RevertAdminPunchAdjustTests : SqliteTestBase
{
    private static DateTime At(int h, int m) => Tue.ToDateTime(new TimeOnly(h, m));

    private async Task<int> SeedDayWithoutMidAsync()
    {
        var (uid, _) = SeedDayMidWorld();
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await sync.ProcessAttLogAsync("SNM", [new ZKAttLogRow("M1", At(8, 25), 0, 15), new ZKAttLogRow("M1", At(17, 40), 0, 15)]);
        return uid;
    }

    private AttendanceService Svc(AttendanceDbContext db) => new(db, AppOptions, NullLogger<AttendanceService>.Instance);

    private async Task<int> LatestLogIdAsync(int uid)
    {
        using var db = CreateContext();
        return await db.PunchAdjustLogs.Where(l => l.UserId == uid).OrderByDescending(l => l.Id).Select(l => l.Id).FirstAsync();
    }

    [Fact]
    public async Task 撤回午间补卡_午间打卡删掉_午间命中和工时回到补卡前_上下班时间不变()
    {
        var uid = await SeedDayWithoutMidAsync();
        decimal hoursBefore;
        using (var db = CreateContext())
            hoursBefore = (await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue)).ActualWorkHours;
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.MidCheck, At(12, 40), false, null, "郑小利");
        var logId = await LatestLogIdAsync(uid);
        using (var db = CreateContext())
            await Svc(db).RevertAdminPunchAdjustAsync(logId, "罗佳蓉");

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(hoursBefore, rec.ActualWorkHours);
        Assert.True(rec.ActualWorkHours < 8m);
        Assert.DoesNotContain("12:40", rec.MidCheckResults ?? "");
        Assert.Equal(new TimeOnly(8, 25), TimeOnly.FromDateTime(rec.ClockInTime!.Value));
        Assert.Equal(new TimeOnly(17, 40), TimeOnly.FromDateTime(rec.ClockOutTime!.Value));
        Assert.Contains("已撤回", rec.ApprovalNote);
        Assert.False(await check.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchType == PunchType.MidCheck));
        var log = await check.PunchAdjustLogs.SingleAsync(l => l.Id == logId);
        Assert.NotNull(log.RevertedAt);
        Assert.Equal("罗佳蓉", log.RevertedBy);
        // 不能撤回两次
        using var db2 = CreateContext();
        await Assert.ThrowsAsync<BusinessException>(() => Svc(db2).RevertAdminPunchAdjustAsync(logId, "罗佳蓉"));
    }

    [Fact]
    public async Task 撤回强制覆盖的上班卡_上班时间和状态恢复()
    {
        var uid = await SeedDayWithoutMidAsync();
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockIn, At(10, 0), true, null, "管理员");   // 强制改成 10:00
        using (var db = CreateContext())
            Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime((await db.AttendanceRecords.SingleAsync(r => r.UserId == uid)).ClockInTime!.Value));
        using (var db = CreateContext())
            await Svc(db).RevertAdminPunchAdjustAsync(await LatestLogIdAsync(uid), "管理员");

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(new TimeOnly(8, 25), TimeOnly.FromDateTime(rec.ClockInTime!.Value));
        Assert.Equal(0, rec.LateMinutes);
    }

    [Fact]
    public async Task 补卡之后考勤机又补传了午间卡_撤回补卡_午间命中和工时按现有打卡重算不回到旧值()
    {
        var uid = await SeedDayWithoutMidAsync();
        decimal hoursNoMid;
        using (var db = CreateContext())
            hoursNoMid = (await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue)).ActualWorkHours;
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockIn, At(8, 0), true, null, "管理员");   // 强制补上班卡 08:00
        var logId = await LatestLogIdAsync(uid);
        using (var db = CreateContext())   // 补卡之后，考勤机补传了一次午间卡
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await sync.ProcessAttLogAsync("SNM", [new ZKAttLogRow("M1", At(12, 40), 0, 15)]);
        }
        using (var db = CreateContext())
            await Svc(db).RevertAdminPunchAdjustAsync(logId, "管理员");

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(new TimeOnly(8, 25), TimeOnly.FromDateTime(rec.ClockInTime!.Value));
        Assert.Contains("12:40", rec.MidCheckResults ?? "");   // 午间卡还在，不能被改回"未打"
        Assert.True(rec.ActualWorkHours > hoursNoMid, "午间卡已经补上，工时不能退回没有午间卡时的旧值");
    }

    [Fact]
    public async Task 补卡之后这天又批了请假_撤回补卡_请假状态和请假小时不丢()
    {
        var uid = await SeedDayWithoutMidAsync();
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.MidCheck, At(12, 40), false, null, "管理员");
        var logId = await LatestLogIdAsync(uid);
        using (var db = CreateContext())   // 补卡之后批了半天假（效果同请假审批写回）
        {
            var r = await db.AttendanceRecords.SingleAsync(x => x.UserId == uid && x.WorkDate == Tue);
            r.AttendanceStatus = AttendanceStatus.OnLeave; r.LeaveHours = 4m; r.ActualWorkHours = 4m;
            await db.SaveChangesAsync();
        }
        using (var db = CreateContext())
            await Svc(db).RevertAdminPunchAdjustAsync(logId, "管理员");

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(AttendanceStatus.OnLeave, rec.AttendanceStatus);
        Assert.Equal(4m, rec.LeaveHours);
        Assert.NotNull((await check.PunchAdjustLogs.SingleAsync(l => l.Id == logId)).RevertedAt);
    }

    [Fact]
    public async Task 请假在补卡之前就批了_补上班卡后撤回_工时恢复成补卡前的值()
    {
        var (uid, _) = SeedDayMidWorld();
        using (var db = CreateContext())   // 上午请了半天假，下午只打了下班卡 17:40，没有上班卡
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, ClockOutTime = At(17, 40),
                AttendanceStatus = AttendanceStatus.OnLeave, LeaveHours = 4m, ActualWorkHours = 0m });
            await db.SaveChangesAsync();
        }
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockIn, At(13, 0), false, null, "管理员");
        using (var db = CreateContext())
            await Svc(db).RevertAdminPunchAdjustAsync(await LatestLogIdAsync(uid), "管理员");

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Null(rec.ClockInTime);
        Assert.Equal(AttendanceStatus.OnLeave, rec.AttendanceStatus);
        Assert.Equal(0m, rec.ActualWorkHours);   // 补卡才算出来的工时要撤掉
    }

    [Fact]
    public async Task 撤回手动补卡建出来的记录_这天原来没有记录_撤回后记录删除()
    {
        var (uid, _) = SeedDayMidWorld();
        using (var db = CreateContext())
            await Svc(db).AdminAdjustPunchAsync(uid, Tue, At(8, 30), At(17, 30), "补录", "管理员");
        using (var db = CreateContext())
            Assert.True(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
        using (var db = CreateContext())
            await Svc(db).RevertAdminPunchAdjustAsync(await LatestLogIdAsync(uid), "管理员");
        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
    }

    [Fact]
    public async Task 同一天补了两次_只能先撤回更晚的那次()
    {
        var uid = await SeedDayWithoutMidAsync();
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.MidCheck, At(12, 40), false, null, "管理员");
        var first = await LatestLogIdAsync(uid);
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(19, 0), false, null, "管理员");
        var second = await LatestLogIdAsync(uid);

        using (var db = CreateContext())
            await Assert.ThrowsAsync<BusinessException>(() => Svc(db).RevertAdminPunchAdjustAsync(first, "管理员"));   // 先撤回更晚的
        using (var db = CreateContext())
            await Svc(db).RevertAdminPunchAdjustAsync(second, "管理员");
        using (var db = CreateContext())
            await Svc(db).RevertAdminPunchAdjustAsync(first, "管理员");
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(new TimeOnly(17, 40), TimeOnly.FromDateTime(rec.ClockOutTime!.Value));
    }

    [Fact]
    public async Task 补卡之后上下班时间又被别的操作改过_不能直接撤回()
    {
        var uid = await SeedDayWithoutMidAsync();
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(19, 0), false, null, "管理员");
        var logId = await LatestLogIdAsync(uid);
        using (var db = CreateContext())
        {
            var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
            rec.ClockOutTime = At(21, 0);   // 之后被别的操作改了
            await db.SaveChangesAsync();
        }
        using var db2 = CreateContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => Svc(db2).RevertAdminPunchAdjustAsync(logId, "管理员"));
        Assert.Contains("改过", ex.Message);
    }
}
