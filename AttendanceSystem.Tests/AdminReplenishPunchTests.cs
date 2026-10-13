using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>管理员手动补卡页：能做员工自己补卡能做的全部（上班 / 下班 / 午间），规则一致；要覆盖已有时间必须"强制覆盖"。</summary>
public class AdminReplenishPunchTests : SqliteTestBase
{
    private static DateTime At(int h, int m) => Tue.ToDateTime(new TimeOnly(h, m));

    /// <summary>考勤机同步出：08:25 上班、17:40 下班，中午（窗口 12:00–13:00）没打卡 → 当天只算半天。</summary>
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

    [Fact]
    public async Task 管理员补午间卡_只新增一条午间打卡_上下班时间不动_午间命中_工时恢复()
    {
        var uid = await SeedDayWithoutMidAsync();
        using (var db = CreateContext())
        {
            var before = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
            Assert.True(before.ActualWorkHours < 8m);   // 漏了午间窗口：半天
        }
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.MidCheck, At(12, 40), false, "漏打午间卡", "罗佳蓉");

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(new TimeOnly(8, 25), TimeOnly.FromDateTime(rec.ClockInTime!.Value));
        Assert.Equal(new TimeOnly(17, 40), TimeOnly.FromDateTime(rec.ClockOutTime!.Value));
        Assert.Contains("12:40", rec.MidCheckResults);
        Assert.Equal(8m, rec.ActualWorkHours);
        Assert.Contains("管理员手动补卡（操作人：罗佳蓉）：补午间卡 12:40", rec.ApprovalNote);
        var punch = await check.AttendancePunches.SingleAsync(p => p.UserId == uid && p.PunchType == PunchType.MidCheck);
        Assert.Equal(At(12, 40), punch.PunchTime);
        Assert.Contains("管理员补卡", punch.DeviceInfo);
    }

    [Fact]
    public async Task 管理员补午间卡_不在窗口内_或这段已经有卡_或早于上班卡_都拒绝并说明原因()
    {
        var uid = await SeedDayWithoutMidAsync();
        using (var db = CreateContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.MidCheck, At(14, 0), false, null, "管理员"));
            Assert.Contains("12:00–13:00", ex.Message);                     // 不在窗口里
        }
        using (var db = CreateContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.MidCheck, At(8, 0), false, null, "管理员"));
            Assert.Contains("上班卡", ex.Message);                           // 早于上班卡
        }
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.MidCheck, At(12, 30), false, null, "管理员");
        using (var db = CreateContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.MidCheck, At(12, 50), false, null, "管理员"));
            Assert.Contains("已经有午间打卡", ex.Message);                    // 这一段已经命中了
        }
    }

    [Fact]
    public async Task 管理员补上班卡_比现有的晚_不生效_勾强制覆盖才改_且改后上下班顺序仍要对()
    {
        var uid = await SeedDayWithoutMidAsync();
        // 刘育辰那种情况：已有 08:25 的上班卡，又想"补"12:56 当上班卡 → 以前直接覆盖，现在拒绝
        using (var db = CreateContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockIn, At(12, 56), false, null, "管理员"));
            Assert.Contains("午间打卡", ex.Message);                         // 提示改选"午间打卡"
        }
        using (var db = CreateContext())
        {
            var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
            Assert.Equal(new TimeOnly(8, 25), TimeOnly.FromDateTime(rec.ClockInTime!.Value));   // 没被覆盖
        }
        // 强制覆盖：允许改成更晚，但仍不能晚于已有的下班卡
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockIn, At(8, 45), true, "原来填错", "管理员");
        using (var db = CreateContext())
        {
            var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
            Assert.Equal(new TimeOnly(8, 45), TimeOnly.FromDateTime(rec.ClockInTime!.Value));
            await Assert.ThrowsAsync<BusinessException>(() =>
                Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockIn, At(18, 0), true, null, "管理员"));   // 晚于下班卡，强制也不行
        }
    }

    [Fact]
    public async Task 管理员补下班卡_比现有的早_不生效_比现有的晚才生效_不能早于上班卡()
    {
        var uid = await SeedDayWithoutMidAsync();
        using (var db = CreateContext())
            await Assert.ThrowsAsync<BusinessException>(() =>
                Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(12, 40), false, null, "管理员"));   // 比 17:40 早
        using (var db = CreateContext())
            await Assert.ThrowsAsync<BusinessException>(() =>
                Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(8, 0), true, null, "管理员"));      // 早于上班卡，强制也不行
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(19, 0), false, null, "管理员");
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(new TimeOnly(19, 0), TimeOnly.FromDateTime(rec.ClockOutTime!.Value));
    }

    [Fact]
    public async Task 管理员补卡_备注超长_未来时间_日期不对_都拒绝()
    {
        var uid = await SeedDayWithoutMidAsync();
        using var db = CreateContext();
        var svc = Svc(db);
        await Assert.ThrowsAsync<BusinessException>(() => svc.AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(19, 0), false, new string('x', 101), "管理员"));
        await Assert.ThrowsAsync<BusinessException>(() => svc.AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, DateTime.Now.AddHours(3), false, null, "管理员"));
        await Assert.ThrowsAsync<BusinessException>(() => svc.AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, Tue.AddDays(-3).ToDateTime(new TimeOnly(19, 0)), false, null, "管理员"));
    }
}
