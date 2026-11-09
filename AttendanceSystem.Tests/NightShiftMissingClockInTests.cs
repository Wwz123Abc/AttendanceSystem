using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 夜班漏打上班卡时，次日早上补打的下班卡不能被当成"很晚的上班卡"（2026-11-09 肖臻 11-07 的问题）：
/// 打卡时间已经过了这个夜班的应下班时间，就只能记成下班卡，缺的上班卡走补卡流程。
/// </summary>
public class NightShiftMissingClockInTests : SqliteTestBase
{
    private static readonly DateOnly NightDate = new(2026, 11, 7);   // 周六上晚班

    private int SeedNightEmployee(string employeeNo)
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "夜班漏卡测试组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var shift = NightShift();                        // 跨天
        shift.WorkStartTime = new TimeOnly(20, 30);
        shift.WorkEndTime   = new TimeOnly(5, 30);
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        db.SaveChanges();

        var user = U(employeeNo, "夜班漏卡测试");
        user.AttendanceGroupId = group.Id;
        user.HireDate = new DateOnly(2026, 1, 1);
        db.Users.Add(user);
        db.SaveChanges();

        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = NightDate, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return user.Id;
    }

    private AttendanceService SvcAt(AttendanceDbContext db, DateTime fixedNow) =>
        new(db, AppOptions, NullLogger<AttendanceService>.Instance, new FixedClock(fixedNow));

    [Fact]
    public async Task 夜班漏打上班卡_次日早上的卡记成下班卡()
    {
        var uid = SeedNightEmployee("NS1");
        using var db = CreateContext();

        // 现在是 11-08 08:30（应下班 11-08 05:30 已经过了）
        var resp = await SvcAt(db, new DateTime(2026, 11, 8, 8, 30, 0))
            .PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockIn }, skipLocationCheck: true);
        Assert.True(resp.Success, resp.Message);

        db.ChangeTracker.Clear();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == NightDate);
        Assert.Null(rec.ClockInTime);                                        // 上班卡仍然缺（走补卡）
        Assert.Equal(new DateTime(2026, 11, 8, 8, 30, 0), rec.ClockOutTime);  // 这张记成了下班卡

        var punch = await db.AttendancePunches.SingleAsync(p => p.UserId == uid);
        Assert.Equal(PunchType.ClockOut, punch.PunchType);                   // 流水类型也跟着纠正
    }

    [Fact]
    public async Task 夜班凌晨才到_仍记成晚到的上班卡()
    {
        var uid = SeedNightEmployee("NS2");
        using var db = CreateContext();

        // 现在是 11-08 00:30（还在班内，只是迟到了）
        var resp = await SvcAt(db, new DateTime(2026, 11, 8, 0, 30, 0))
            .PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockIn }, skipLocationCheck: true);
        Assert.True(resp.Success, resp.Message);

        db.ChangeTracker.Clear();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == NightDate);
        Assert.Equal(new DateTime(2026, 11, 8, 0, 30, 0), rec.ClockInTime);   // 正常记上班卡
        Assert.Null(rec.ClockOutTime);
    }

    [Theory]
    [InlineData(2026, 11, 8, 8, 30, true)]    // 过了应下班 05:30 → 不可能是上班卡
    [InlineData(2026, 11, 8, 5, 30, true)]    // 正好应下班 → 不可能是上班卡
    [InlineData(2026, 11, 8, 0, 30, false)]   // 班次时间内 → 可能是迟到的上班卡
    [InlineData(2026, 11, 7, 20, 0, false)]   // 上班之前 → 正常上班卡
    public void 过了应下班时间的判定(int year, int month, int day, int hour, int minute, bool expected)
    {
        var shift = NightShift();
        shift.WorkStartTime = new TimeOnly(20, 30);
        shift.WorkEndTime   = new TimeOnly(5, 30);
        Assert.Equal(expected, AttendanceService.IsFirstPunchAfterShiftEnd(
            NightDate, new DateTime(year, month, day, hour, minute, 0), shift, isRestDay: false));
    }
}
