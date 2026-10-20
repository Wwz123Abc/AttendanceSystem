using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>午间窗口过去之后才打的卡：显示"迟到 hh:mm"，而不是"未打卡"；只改展示，工时口径不变。</summary>
public class LateMidCheckDisplayTests : SqliteTestBase
{
    private static DateTime At(DateOnly d, int h, int m) => d.ToDateTime(new TimeOnly(h, m));

    private static List<MidCheckWindowResult> OneWindow() => [new(new TimeOnly(12, 0), new TimeOnly(13, 0), null)];

    [Fact]
    public void 窗口结束后的第一次午间打卡_算迟到_并算出晚了几分钟()
    {
        var shift = DayShift();   // 08:30–17:30
        var r = AttendanceService.FindLateMidChecks(Tue, shift, OneWindow(), At(Tue, 17, 40), [At(Tue, 8, 25), At(Tue, 13, 20), At(Tue, 17, 40)]);
        var w = Assert.Single(r);
        Assert.True(w.IsLate);
        Assert.Equal(new TimeOnly(13, 20), w.LateTime);
        Assert.Equal(20, w.LateMinutes);
        Assert.False(w.IsSatisfied);                      // 仍然没命中窗口（工时口径不变）
    }

    [Fact]
    public void 上班就晚于午间窗口_那张上班卡不算午间迟到()
    {
        var shift = DayShift();   // 08:30–17:30
        List<MidCheckWindowResult> w = [new(new TimeOnly(11, 30), new TimeOnly(12, 30), null)];
        // 上午请假、13:00 才来上班：当天只有 13:00 上班卡和 17:30 下班卡
        var r = AttendanceService.FindLateMidChecks(Tue, shift, w, At(Tue, 17, 30), [At(Tue, 13, 0), At(Tue, 17, 30)], At(Tue, 13, 0));
        Assert.False(Assert.Single(r).IsLate);
        // 记录上的上班时间带秒（13:00:20），流水精确到分钟（13:00），同一分钟也不算
        var r2 = AttendanceService.FindLateMidChecks(Tue, shift, w, At(Tue, 17, 30), [At(Tue, 13, 0), At(Tue, 17, 30)], At(Tue, 13, 0).AddSeconds(20));
        Assert.False(Assert.Single(r2).IsLate);
        // 上班卡之后、窗口结束之后真有一次午间卡（13:20），仍然算迟到
        var r3 = AttendanceService.FindLateMidChecks(Tue, shift, w, At(Tue, 17, 30), [At(Tue, 8, 25), At(Tue, 13, 20), At(Tue, 17, 30)], At(Tue, 8, 25));
        Assert.Equal(new TimeOnly(13, 20), Assert.Single(r3).LateTime);
    }

    [Fact]
    public async Task 我的记录_上班晚于午间窗口_午间显示未打卡_不显示迟到()
    {
        var (uid, _) = SeedDayMidWorld();    // 窗口 12:00–13:00
        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await sync.ProcessAttLogAsync("SNM", [
                new ZKAttLogRow("M1", At(Tue, 13, 45), 0, 15),
                new ZKAttLogRow("M1", At(Tue, 17, 40), 0, 15)]);
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var rec = Assert.Single(await svc.GetPersonalAttendanceAsync(new PersonalAttendanceQueryDto { UserId = uid, StartDate = Tue, EndDate = Tue }));
        Assert.False(Assert.Single(rec.MidCheckHits).IsLate);
        Assert.Equal("--", rec.MidCheckTimeText);
    }

    [Fact]
    public void 没有午间打卡_只有上下班卡_仍然是未打卡_不会把下班卡当成迟到的午间卡()
    {
        var shift = DayShift();
        var r = AttendanceService.FindLateMidChecks(Tue, shift, OneWindow(), At(Tue, 17, 40), [At(Tue, 8, 25), At(Tue, 17, 40)]);
        Assert.False(Assert.Single(r).IsLate);
        // 离下班已经不到 2 小时的打卡（15:40 之后）归下班那一次，也不算午间迟到
        var r2 = AttendanceService.FindLateMidChecks(Tue, shift, OneWindow(), null, [At(Tue, 8, 25), At(Tue, 16, 10)]);
        Assert.False(Assert.Single(r2).IsLate);
    }

    [Fact]
    public void 已命中的窗口不动_多段窗口各找各的_不会拿下一段窗口里的卡当上一段的迟到()
    {
        var shift = DayShift();
        List<MidCheckWindowResult> two =
        [
            new(new TimeOnly(10, 0), new TimeOnly(10, 30), null),
            new(new TimeOnly(12, 0), new TimeOnly(13, 0), new TimeOnly(12, 15)),
        ];
        // 10:50 才打：算第一段迟到；12:15 是第二段命中的卡，不能算成第一段的迟到（它在第二段窗口开始之后）
        var r = AttendanceService.FindLateMidChecks(Tue, shift, two, At(Tue, 17, 40), [At(Tue, 8, 25), At(Tue, 10, 50), At(Tue, 12, 15), At(Tue, 17, 40)]);
        Assert.Equal(new TimeOnly(10, 50), r[0].LateTime);
        Assert.True(r[1].IsSatisfied);
        Assert.False(r[1].IsLate);
    }

    [Fact]
    public void 夜班窗口在午夜之后_迟到按班次推算到第二天()
    {
        var night = NightShift();   // 20:00 上班、次日 08:00 下班
        List<MidCheckWindowResult> w = [new(new TimeOnly(0, 0), new TimeOnly(1, 0), null)];
        var r = AttendanceService.FindLateMidChecks(Tue, night, w, At(Tue.AddDays(1), 8, 5),
            [At(Tue, 20, 5), At(Tue.AddDays(1), 1, 20), At(Tue.AddDays(1), 8, 5)]);
        Assert.Equal(new TimeOnly(1, 20), Assert.Single(r).LateTime);
        Assert.Equal(20, r[0].LateMinutes);
    }

    [Fact]
    public async Task 我的记录_午间超出窗口_显示迟到和打卡时间_工时仍按漏打算()
    {
        var (uid, _) = SeedDayMidWorld();    // 窗口 12:00–13:00
        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await sync.ProcessAttLogAsync("SNM", [
                new ZKAttLogRow("M1", At(Tue, 8, 25), 0, 15),
                new ZKAttLogRow("M1", At(Tue, 13, 40), 0, 15),
                new ZKAttLogRow("M1", At(Tue, 17, 40), 0, 15)]);
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var rec = Assert.Single(await svc.GetPersonalAttendanceAsync(new PersonalAttendanceQueryDto { UserId = uid, StartDate = Tue, EndDate = Tue }));
        var hit = Assert.Single(rec.MidCheckHits);
        Assert.True(hit.IsLate);
        Assert.Equal(new TimeOnly(13, 40), hit.LateTime);
        Assert.Equal("迟到 13:40", rec.MidCheckTimeText);
        Assert.Equal("12:00-13:00: 迟到 13:40", rec.MidCheckTexts.Single());
        Assert.True(rec.ActualWorkHours < 8m);            // 工时口径没变：窗口没打上仍然只算半天
    }

    [Fact]
    public async Task 打卡时间明细导出和部门考勤列表_也带迟到标记()
    {
        var (uid, _) = SeedDayMidWorld();
        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await sync.ProcessAttLogAsync("SNM", [
                new ZKAttLogRow("M1", At(Tue, 8, 25), 0, 15),
                new ZKAttLogRow("M1", At(Tue, 13, 40), 0, 15),
                new ZKAttLogRow("M1", At(Tue, 17, 40), 0, 15)]);
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var sheet = Assert.Single(await svc.GetClockTimeSheetAsync(Tue, Tue, null));
        Assert.Equal("迟到 13:40", sheet.MidCheckTimeText);
        var dept = Assert.Single(await svc.GetDeptAttendanceAsync(new DeptAttendanceQueryDto { StartDate = Tue, EndDate = Tue }));
        Assert.Equal("迟到 13:40", dept.MidCheckTimeText);
    }
}
