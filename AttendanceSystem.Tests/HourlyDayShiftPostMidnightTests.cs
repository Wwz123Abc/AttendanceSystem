using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-11-20：计时工白班干到次日凌晨才下班（售后组临时工经常 0 点以后下班）。
/// 原规则把 00:00–07:00 的打卡一律判无效，导致下班卡打不上、工时拿不到；
/// 现在只要前一天确实上了白班，次日 02:00 前的下班卡归前一天，02:00 以后和前一天没上班的仍然无效。
/// </summary>
public class HourlyDayShiftPostMidnightTests : SqliteTestBase
{
    private static DateTime At(DateOnly day, int h, int m) => day.ToDateTime(new TimeOnly(h, m));

    private (int uid, int groupId) SeedHourlyDay()
    {
        var (uid, _) = SeedDayMidWorld();   // 周二白班 08:30–17:30、午间窗口 12:00–13:00、工号 M1、设备 SNM
        using var db = CreateContext();
        var gid = db.Users.Single(u => u.Id == uid).AttendanceGroupId!.Value;
        db.AttendanceGroups.Single(g => g.Id == gid).HourlyWorkStartDate = Tue;
        db.SaveChanges();
        return (uid, gid);
    }

    private async Task SyncAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await sync.ProcessAttLogAsync("SNM", times.Select(t => new ZKAttLogRow("M1", t, 0, 15)).ToList());
    }

    private async Task<AttendanceRecord> RecordAsync(int uid, DateOnly day)
    {
        using var db = CreateContext();
        return await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == day);
    }

    private async Task<PunchResponseDto> MobilePunchAsync(int uid, DateTime when, PunchType type)
    {
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance, new FixedClock(when));
        return await svc.PunchAsync(uid, new PunchRequestDto { PunchType = type }, skipLocationCheck: true);
    }

    [Fact]
    public async Task 白班干到次日0点30_考勤机打卡归前一天_工时按实际算()
    {
        var (uid, _) = SeedHourlyDay();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(Tue, 8, 25), At(Tue, 12, 20), At(wed, 0, 30));

        var rec = await RecordAsync(uid, Tue);
        Assert.Equal(At(Tue, 8, 25), rec.ClockInTime);
        Assert.Equal(At(wed, 0, 30), rec.ClockOutTime);   // 次日 0:30 算前一天的下班卡
        Assert.InRange(rec.ActualWorkHours, 14m, 14.1m);  // 08:25–次日00:30，扣午饭/晚饭/零点后饭点共 2 小时 = 14.08 → 14.0

        using var db = CreateContext();
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == wed));
    }

    [Fact]
    public async Task 前一天没有上班卡_凌晨的卡仍然无效()
    {
        var (uid, _) = SeedHourlyDay();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(wed, 0, 30));

        using var db = CreateContext();
        Assert.False(await db.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchTime == At(wed, 0, 30)));
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == wed));
    }

    [Fact]
    public async Task 次日2点以后的下班卡仍然无效()
    {
        var (uid, _) = SeedHourlyDay();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(Tue, 8, 25), At(wed, 2, 30));

        var rec = await RecordAsync(uid, Tue);
        Assert.Null(rec.ClockOutTime);
        using var db = CreateContext();
        Assert.False(await db.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchTime == At(wed, 2, 30)));
    }

    [Fact]
    public async Task 次日7点后的正常上班卡不受影响()
    {
        var (uid, _) = SeedHourlyDay();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(wed, 8, 20));

        var rec = await RecordAsync(uid, wed);
        Assert.Equal(At(wed, 8, 20), rec.ClockInTime);
        Assert.Null(rec.ClockOutTime);
    }

    [Fact]
    public async Task 手机打卡次日0点30也归前一天()
    {
        var (uid, _) = SeedHourlyDay();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(Tue, 8, 25));

        var resp = await MobilePunchAsync(uid, At(wed, 0, 30), PunchType.ClockOut);
        Assert.True(resp.Success, resp.Message);

        var rec = await RecordAsync(uid, Tue);
        Assert.Equal(At(wed, 0, 30), rec.ClockOutTime);
        using var db = CreateContext();
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == wed));
    }

    [Fact]
    public async Task 凌晨卡不会在次日今天排夜班时被吞到前一天()
    {
        // 今天排夜班（19:00 才开始），凌晨 0:30 的卡既不可能是今天的上班卡，也不该被当成前一天白班的下班卡
        Assert.Null(AttendanceService.ResolveHourlyWorkDate(
            At(Tue.AddDays(1), 0, 30), NightShift(), DayShift(), At(Tue, 8, 25)));
    }

    [Fact]
    public void 补卡规则_允许次日0点30下班卡_2点30和没有上班卡仍拒绝()
    {
        var shift = DayShift();
        var clockIn = At(Tue, 8, 25);

        var at = PunchReplenishmentRules.ResolveTime(PunchType.ClockOut, Tue, new TimeOnly(0, 30), clockIn, shift, hourly: true);
        Assert.Equal(At(Tue.AddDays(1), 0, 30), at);

        var ctx = new ReplenishmentContext(shift, clockIn, null, null, null, [], true);
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.ClockOut, Tue, at, ctx));

        var noClockIn = ctx with { ClockIn = null };
        Assert.NotNull(PunchReplenishmentRules.FindProblem(PunchType.ClockOut, Tue, at, noClockIn));

        Assert.NotNull(PunchReplenishmentRules.FindProblem(
            PunchType.ClockOut, Tue, At(Tue.AddDays(1), 2, 30), ctx));
    }

    [Fact]
    public async Task 审批补下班卡后_次日凌晨卡不能再延长工时()
    {
        var (uid, _) = SeedHourlyDay();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(Tue, 8, 25), At(Tue, 12, 20));

        int requestId;
        using (var db = CreateContext())
        {
            var req = new ApprovalRequest
            {
                RequestNo = "BK-HOURLY-CLOSED", ApplicantUserId = uid,
                ApprovalType = ApprovalType.PunchReplenishment, ApprovalStatus = ApprovalStatus.Approved,
                PunchDate = Tue, PunchType = PunchType.ClockOut, PunchTime = new TimeOnly(17, 30),
                SubmittedAt = At(Tue, 12, 30), UpdatedAt = At(Tue, 12, 30)
            };
            db.ApprovalRequests.Add(req);
            await db.SaveChangesAsync();
            requestId = req.Id;
        }
        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance)
                .UpdateAttendanceAfterApprovalAsync(requestId);

        using (var db = CreateContext())
        {
            var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
            Assert.Equal(At(Tue, 17, 30), rec.ClockOutTime);
            Assert.True(await db.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchType == PunchType.ClockOut
                && p.PunchTime == At(Tue, 17, 30)));
        }

        await SyncAsync(At(wed, 0, 30));

        var after = await RecordAsync(uid, Tue);
        Assert.Equal(At(Tue, 17, 30), after.ClockOutTime);
        using var check = CreateContext();
        Assert.False(await check.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchTime == At(wed, 0, 30)));
    }

    [Fact]
    public async Task 管理员补下班卡后_次日凌晨卡不能再延长工时()
    {
        var (uid, _) = SeedHourlyDay();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(Tue, 8, 25), At(Tue, 12, 20));
        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance)
                .AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(Tue, 17, 30), false, null, "管理员");

        using (var db = CreateContext())
        {
            var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
            Assert.Equal(At(Tue, 17, 30), rec.ClockOutTime);
            Assert.True(await db.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchType == PunchType.ClockOut
                && p.PunchTime == At(Tue, 17, 30)));
        }

        await SyncAsync(At(wed, 0, 30));   // 补出来的下班卡也要被识别为"已下班"

        var after = await RecordAsync(uid, Tue);
        Assert.Equal(At(Tue, 17, 30), after.ClockOutTime);   // 不能被凌晨卡延长
        using var check = CreateContext();
        Assert.False(await check.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchTime == At(wed, 0, 30)));
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == wed));
    }

    [Fact]
    public async Task 前一天的凌晨下班卡_不会把后一天误标成已下班()
    {
        var (uid, _) = SeedHourlyDay();
        var wed = Tue.AddDays(1);
        var thu = wed.AddDays(1);
        using (var db = CreateContext())
        {
            var shiftId = db.ShiftAssignments.Single(a => a.UserId == uid && a.WorkDate == Tue).ShiftScheduleId;
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = uid, WorkDate = wed, ShiftScheduleId = shiftId });
            await db.SaveChangesAsync();
        }

        await SyncAsync(
            At(Tue, 8, 25), At(Tue, 12, 20), At(wed, 0, 30),   // 周二班，周三 0:30 下班
            At(wed, 8, 25), At(wed, 12, 20), At(thu, 0, 30));   // 周三班，周四 0:30 下班

        var tueRec = await RecordAsync(uid, Tue);
        var wedRec = await RecordAsync(uid, wed);
        Assert.Equal(At(wed, 0, 30), tueRec.ClockOutTime);
        Assert.Equal(At(thu, 0, 30), wedRec.ClockOutTime);       // 不能被周二那条 0:30 的卡误判成"周三已下班"
        using var db2 = CreateContext();
        Assert.False(await db2.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == thu));
    }

    [Fact]
    public async Task 前一天已经正常下班_凌晨再打卡不能覆盖下班时间()
    {
        var (uid, _) = SeedHourlyDay();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(Tue, 8, 25), At(Tue, 12, 20), At(Tue, 17, 30), At(wed, 0, 30));

        var rec = await RecordAsync(uid, Tue);
        Assert.Equal(At(Tue, 8, 25), rec.ClockInTime);
        Assert.Equal(At(Tue, 17, 30), rec.ClockOutTime);   // 已结束的记录不能被凌晨卡延长/覆盖

        using var db = CreateContext();
        Assert.False(await db.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchTime == At(wed, 0, 30)));
    }

    [Fact]
    public async Task 历史重建_把次日0点30的下班卡归到前一天()
    {
        var (uid, gid) = SeedHourlyDay();
        var wed = Tue.AddDays(1);

        using (var db = CreateContext())
        {
            db.AttendancePunches.AddRange(
                new AttendancePunch { UserId = uid, PunchTime = At(Tue, 8, 25), PunchType = PunchType.ClockIn, IsValid = true },
                new AttendancePunch { UserId = uid, PunchTime = At(Tue, 12, 20), PunchType = PunchType.MidCheck, IsValid = true },
                new AttendancePunch { UserId = uid, PunchTime = At(wed, 0, 30), PunchType = PunchType.ClockOut, IsValid = true });
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.RebuildHourlyRecordsAsync([gid], Tue, Tue, dryRun: false);
        }

        var rec = await RecordAsync(uid, Tue);
        Assert.Equal(At(Tue, 8, 25), rec.ClockInTime);
        Assert.Equal(At(wed, 0, 30), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 14m, 14.1m);

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == wed));
    }
}
