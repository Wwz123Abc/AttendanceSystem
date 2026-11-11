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
/// 生效日 2026-10-24；普通员工和计时工都适用。
/// </summary>
public class MidCheckAsClockOutTests : SqliteTestBase
{
    private static readonly DateOnly Nov3  = new(2026, 11, 3);    // 生效日（10-24）之后
    private static readonly DateOnly Oct23 = new(2026, 10, 23);   // 生效日之前

    private (int uid, int gid) Seed(DateOnly date, bool withClockOut = false, bool hourly = false)
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "白班组" };
        if (hourly) group.HourlyWorkStartDate = new DateOnly(2026, 10, 24);
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
        var (uid, _) = Seed(Oct23);
        using var db = CreateContext();
        Assert.False(await Svc(db).SettleHalfDayByMidCheckAsync(uid, Oct23));

        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Oct23);
        Assert.Equal(0m, rec.ActualWorkHours);
        Assert.Equal(AttendanceStatus.NotPunched, rec.AttendanceStatus);
    }

    [Fact]
    public async Task 计时工同样按半天算()
    {
        var (uid, _) = Seed(Nov3, hourly: true);
        using var db = CreateContext();
        Assert.True(await Svc(db).SettleHalfDayByMidCheckAsync(uid, Nov3));

        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Nov3);
        Assert.Equal(3.5m, rec.ActualWorkHours);   // 计时工也按"上班卡 → 午间卡"算上半段
        Assert.Equal(AttendanceStatus.EarlyLeave, rec.AttendanceStatus);
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
        Assert.Equal(3.5m, summary!.TotalWorkHours);   // 汇总按 FloorToHalf 累加
    }

    [Fact]
    public async Task 重复补算不会重复追加备注()
    {
        var (uid, _) = Seed(Nov3);
        using var db = CreateContext();
        var svc = Svc(db);

        Assert.True(await svc.SettleHalfDayByMidCheckAsync(uid, Nov3));
        await db.SaveChangesAsync();   // 结算方法只改实体不落库（正常由补算流程统一保存），测试里要自己存
        var first = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Nov3);
        var noteAfterFirst  = first.ApprovalNote;
        var hoursAfterFirst = first.ActualWorkHours;

        // 第二次直接结算、以及再跑一遍补算，都不应该再动这条记录（否则每次重启都追加一条一样的备注）
        Assert.False(await svc.SettleHalfDayByMidCheckAsync(uid, Nov3));
        Assert.Equal(0, await svc.BackfillMidCheckAsClockOutAsync(new DateOnly(2026, 11, 1), Nov3));

        db.ChangeTracker.Clear();
        var after = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Nov3);
        Assert.Equal(noteAfterFirst, after.ApprovalNote);
        Assert.Equal(hoursAfterFirst, after.ActualWorkHours);
    }

    [Fact]
    public async Task 夜班次日凌晨的午间卡_补算范围要带上前一天才能结算()
    {
        var night = new DateOnly(2026, 11, 5);   // 周四上晚班
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "晚班组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var shift = NightShift();
        shift.WorkStartTime   = new TimeOnly(20, 30);
        shift.WorkEndTime     = new TimeOnly(5, 30);
        shift.MidCheckWindows = "00:00-01:00";       // 次日凌晨的午间窗口
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);

        var user = U("T3", "夜班员工");
        user.AttendanceGroupId = group.Id;
        db.Users.Add(user);
        db.SaveChanges();

        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = night, ShiftScheduleId = shift.Id });
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            UserId = user.Id, WorkDate = night,
            ClockInTime = night.ToDateTime(new TimeOnly(20, 22)),
            AttendanceStatus = AttendanceStatus.NotPunched
        });
        db.AttendancePunches.AddRange(
            new AttendancePunch { UserId = user.Id, PunchTime = night.ToDateTime(new TimeOnly(20, 22)), PunchType = PunchType.ClockIn, IsValid = true },
            // 这张午间卡发生在第二天凌晨，考勤日仍归 11-05
            new AttendancePunch { UserId = user.Id, PunchTime = night.AddDays(1).ToDateTime(new TimeOnly(0, 10)), PunchType = PunchType.MidCheck, IsValid = true });
        db.SaveChanges();

        var svc = Svc(db);
        // 只补"当天"（11-06）结算不到这条 11-05 的记录 —— 这就是夜间任务必须把昨天一起补的原因
        Assert.Equal(0, await svc.BackfillMidCheckAsClockOutAsync(new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 6)));
        // 带上昨天就能结算：20:30 → 次日 00:10
        Assert.Equal(1, await svc.BackfillMidCheckAsClockOutAsync(night, new DateOnly(2026, 11, 6)));

        db.ChangeTracker.Clear();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == user.Id && r.WorkDate == night);
        Assert.Equal(3.5m, rec.ActualWorkHours);
    }

    [Fact]
    public async Task 夜班午夜00点整的午间卡不能当成没打()
    {
        var date = Nov3;   // 周二
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "晚班组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var shift = NightShift();                    // 20:00–次日 08:00，跨天、无固定休息日
        shift.WorkStartTime   = new TimeOnly(20, 30);
        shift.WorkEndTime     = new TimeOnly(5, 30);
        shift.MidCheckWindows = "00:00-01:00";       // 午夜窗口
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);

        var user = U("T2", "夜班员工");
        user.AttendanceGroupId = group.Id;
        db.Users.Add(user);
        db.SaveChanges();

        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = date, ShiftScheduleId = shift.Id });
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            UserId = user.Id, WorkDate = date,
            ClockInTime = date.ToDateTime(new TimeOnly(20, 22)),
            AttendanceStatus = AttendanceStatus.NotPunched
        });
        db.AttendancePunches.AddRange(
            new AttendancePunch { UserId = user.Id, PunchTime = date.ToDateTime(new TimeOnly(20, 22)), PunchType = PunchType.ClockIn,  IsValid = true },
            // 午夜 00:00 整打的午间卡：TimeOnly 默认值就是 00:00，曾被误判成"没打午间卡"
            new AttendancePunch { UserId = user.Id, PunchTime = date.AddDays(1).ToDateTime(new TimeOnly(0, 0)), PunchType = PunchType.MidCheck, IsValid = true });
        db.SaveChanges();

        Assert.True(await Svc(db).SettleHalfDayByMidCheckAsync(user.Id, date));

        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == user.Id && r.WorkDate == date);
        Assert.Equal(3.5m, rec.ActualWorkHours);     // 20:30 → 次日 00:00
        Assert.Equal(AttendanceStatus.EarlyLeave, rec.AttendanceStatus);
    }
    [Fact]
    public async Task 夜班还没到次日续接窗口结束前_不能提前按午间卡结算()
    {
        // 2026-11-11 修复：上午应用重启时，补算曾把 11-10 还在上夜班的人提前按午间卡结算
        //（应下班 05:30，员工 11:30 前还能补打下班卡）。现在必须过了"应下班 + 6 小时"才能结算。
        var night = new DateOnly(2026, 11, 10);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "晚班提前结算组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            var shift = NightShift();
            shift.WorkStartTime   = new TimeOnly(20, 30);
            shift.WorkEndTime     = new TimeOnly(5, 30);
            shift.MidCheckWindows = "00:00-01:00";
            shift.AttendanceGroupId = group.Id;
            db.ShiftSchedules.Add(shift);
            var user = U("T9", "晚班提前结算");
            user.AttendanceGroupId = group.Id;
            db.Users.Add(user);
            db.SaveChanges();
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = night, ShiftScheduleId = shift.Id });
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = user.Id, WorkDate = night,
                ClockInTime = night.ToDateTime(new TimeOnly(20, 10)),
                AttendanceStatus = AttendanceStatus.Normal
            });
            db.AttendancePunches.AddRange(
                new AttendancePunch { UserId = user.Id, PunchTime = night.ToDateTime(new TimeOnly(20, 10)), PunchType = PunchType.ClockIn, IsValid = true },
                new AttendancePunch { UserId = user.Id, PunchTime = night.AddDays(1).ToDateTime(new TimeOnly(0, 10)), PunchType = PunchType.MidCheck, IsValid = true });
            db.SaveChanges();
            uid = user.Id;
        }

        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance,
                new FixedClock(new DateTime(2026, 11, 11, 10, 0, 0)));   // 应下班 05:30 + 6h = 11:30 之前
            Assert.False(await svc.SettleHalfDayByMidCheckAsync(uid, night));
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance,
                new FixedClock(new DateTime(2026, 11, 11, 13, 15, 0)));  // 过了续接窗口（05:30 + 7.5h = 13:00）才允许结算
            Assert.True(await svc.SettleHalfDayByMidCheckAsync(uid, night));
        }
    }
}