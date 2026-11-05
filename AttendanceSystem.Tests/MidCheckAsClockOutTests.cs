using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 没打下班卡、但打了午间卡：把午间卡当成当天下班，只算上半段工时（2026-11-05 用户口径）。
/// 生效日 2026-11-01，之前的日子不套用（避免改 9/10 月已发薪的工时）。
/// </summary>
public class MidCheckAsClockOutTests : SqliteTestBase
{
    private static readonly DateOnly Nov3  = new(2026, 11, 3);    // 生效日之后
    private static readonly DateOnly Oct31 = new(2026, 10, 31);   // 生效日之前

    private (int uid, int gid) Seed(DateOnly date, bool withClockOut = false)
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "白班组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var shift = DayShift();                      // 08:30–17:30
        shift.MidCheckWindows = "12:00-13:00";       // 午间必打卡窗口
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);

        var user = U("T1", "测试员工");
        user.AttendanceGroupId = group.Id;
        db.Users.Add(user);
        db.SaveChanges();

        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = date, ShiftScheduleId = shift.Id });
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            UserId = user.Id, WorkDate = date,
            ClockInTime  = date.ToDateTime(new TimeOnly(8, 20)),
            ClockOutTime = withClockOut ? date.ToDateTime(new TimeOnly(17, 30)) : null,
            AttendanceStatus = withClockOut ? AttendanceStatus.Normal : AttendanceStatus.NotPunched
        });
        db.AttendancePunches.AddRange(
            new AttendancePunch { UserId = user.Id, PunchTime = date.ToDateTime(new TimeOnly(8, 20)),  PunchType = PunchType.ClockIn,  IsValid = true },
            new AttendancePunch { UserId = user.Id, PunchTime = date.ToDateTime(new TimeOnly(12, 26)), PunchType = PunchType.MidCheck, IsValid = true });
        db.SaveChanges();
        return (user.Id, group.Id);
    }

    private AttendanceService Svc(AttendanceDbContext db) => new(db, AppOptions, NullLogger<AttendanceService>.Instance);

    [Fact]
    public async Task 没打下班卡但有午间卡_按午间卡当天下班_只算上半段工时()
    {
        var (uid, _) = Seed(Nov3);
        using var db = CreateContext();
        Assert.True(await Svc(db).SettleHalfDayByMidCheckAsync(uid, Nov3));

        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Nov3);
        Assert.Equal(3.5m, rec.ActualWorkHours);                    // 08:30–12:26，扣掉 12:00–12:26 午休 → 3.5
        Assert.Equal(AttendanceStatus.EarlyLeave, rec.AttendanceStatus);
        Assert.Equal(304, rec.EarlyLeaveMinutes);                   // 17:30 − 12:26
        Assert.Null(rec.ClockOutTime);                              // 不伪造下班打卡时间
        Assert.Contains("午间卡", rec.ApprovalNote);
    }

    [Fact]
    public async Task 生效日之前的日子不结算()
    {
        var (uid, _) = Seed(Oct31);
        using var db = CreateContext();
        Assert.False(await Svc(db).SettleHalfDayByMidCheckAsync(uid, Oct31));

        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Oct31);
        Assert.Equal(0m, rec.ActualWorkHours);
        Assert.Equal(AttendanceStatus.NotPunched, rec.AttendanceStatus);
    }

    [Fact]
    public async Task 已经有下班卡的不动()
    {
        var (uid, _) = Seed(Nov3, withClockOut: true);
        using var db = CreateContext();
        Assert.False(await Svc(db).SettleHalfDayByMidCheckAsync(uid, Nov3));
    }

    [Fact]
    public async Task 批量补算_结算并刷新当月汇总()
    {
        var (uid, _) = Seed(Nov3);
        using (var db = CreateContext())
            Assert.Equal(1, await Svc(db).BackfillMidCheckAsClockOutAsync(new DateOnly(2026, 11, 1), Nov3));

        using var chk = CreateContext();
        var rec = await chk.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Nov3);
        Assert.Equal(3.5m, rec.ActualWorkHours);
        var summary = await chk.MonthlyAttendanceSummaries
            .SingleOrDefaultAsync(s => s.UserId == uid && s.Year == 2026 && s.Month == 11);
        Assert.NotNull(summary);
        Assert.Equal(3.5m, summary!.TotalWorkHours);                // 汇总按 FloorToHalf 累加
    }
}
