using AttendanceSystem.Middlewares;
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
/// 午间必打卡：打完卡"我的记录"里立刻有命中情况（考勤机同步、远程打卡）。
/// </summary>
public class MidCheckPunchTests : SqliteTestBase
{
    // ── ⑨ 午间打卡：打完卡"我的记录"里就要有命中情况，不用等下班卡 ─────────

    [Fact]
    public async Task 考勤机同步_午间打了卡_下班卡还没打_我的记录里就有午间命中()
    {
        var (uid, _) = SeedDayMidWorld();
        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await svc.ProcessAttLogAsync("SNM", [
                new ZKAttLogRow("M1", Tue.ToDateTime(new TimeOnly(8, 25)), 0, 15),    // 上班
                new ZKAttLogRow("M1", Tue.ToDateTime(new TimeOnly(12, 30)), 0, 15)]); // 午间
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Null(rec.ClockOutTime);                                   // 午间卡不是下班卡
        var hits = rec.MidCheckResults.ParseMidCheckResults();
        var hit = Assert.Single(hits);
        Assert.Equal(new TimeOnly(12, 30), hit.HitTime);                 // 以前这里是 null，页面上一直显示 "--"
    }

    [Fact]
    public async Task 远程打卡_上班后打午间卡_立刻写回午间命中_不用等下班卡()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "远程午卡组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            // 窗口设成整天，保证不管测试什么时候跑，"现在"这一次打卡都落在窗口里
            var shift = DayShift(); shift.AttendanceGroupId = group.Id; shift.MidCheckWindows = "00:00-23:59";
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "M2", RealName = "远程员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = today, ShiftScheduleId = shift.Id });
            db.SaveChanges();
            uid = user.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            Assert.True((await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockIn }, skipLocationCheck: true)).Success);
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            Assert.True((await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.MidCheck }, skipLocationCheck: true)).Success);
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == today);
        Assert.Null(rec.ClockOutTime);
        Assert.NotNull(rec.MidCheckResults);
        Assert.True(rec.MidCheckResults.ParseMidCheckResults().Single().IsSatisfied);
    }
}
