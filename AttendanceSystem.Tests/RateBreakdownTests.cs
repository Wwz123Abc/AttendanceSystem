using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>看板出勤率：晚上才上班的夜班在上班时间之前不算应出勤；以及各考勤组 / 各班次的出勤率明细。</summary>
public class RateBreakdownTests : SqliteTestBase
{
    private int _n1;

    /// <summary>周二：白班组两人（D1 08:20 已打卡，D2 没来）、夜班组一人（N1 20:00 上班，还没打卡）、没排班但已打卡一人（X）。</summary>
    private void Seed()
    {
        using var db = CreateContext();
        var g1 = new AttendanceGroup { GroupName = "白班组" };
        var g2 = new AttendanceGroup { GroupName = "夜班组" };
        db.AttendanceGroups.AddRange(g1, g2);
        db.SaveChanges();
        var day = DayShift(); day.AttendanceGroupId = g1.Id;
        var night = NightShift(); night.AttendanceGroupId = g2.Id;
        db.ShiftSchedules.AddRange(day, night);
        var d1 = U("D1", "白班已到"); var d2 = U("D2", "白班没来"); var n1 = U("N1", "夜班"); var x = U("X", "没排班已打卡");
        d1.AttendanceGroupId = g1.Id; d2.AttendanceGroupId = g1.Id; n1.AttendanceGroupId = g2.Id;
        db.Users.AddRange(d1, d2, n1, x);
        db.SaveChanges();
        _n1 = n1.Id;
        db.ShiftAssignments.AddRange(
            new ShiftAssignment { UserId = d1.Id, WorkDate = Tue, ShiftScheduleId = day.Id },
            new ShiftAssignment { UserId = d2.Id, WorkDate = Tue, ShiftScheduleId = day.Id },
            new ShiftAssignment { UserId = n1.Id, WorkDate = Tue, ShiftScheduleId = night.Id });
        db.AttendanceRecords.AddRange(
            new AttendanceRecord { UserId = d1.Id, WorkDate = Tue, ClockInTime = Tue.ToDateTime(new TimeOnly(8, 20)), AttendanceStatus = AttendanceStatus.Normal },
            new AttendanceRecord { UserId = x.Id, WorkDate = Tue, ClockInTime = Tue.ToDateTime(new TimeOnly(8, 0)), AttendanceStatus = AttendanceStatus.Normal });
        db.SaveChanges();
    }

    private static AttendanceService Svc(AttendanceDbContext db, int hour) =>
        new(db, AppOptions, NullLogger<AttendanceService>.Instance, new FixedClock(Tue.ToDateTime(new TimeOnly(hour, 0))));

    [Fact]
    public async Task 上午十点_夜班还没到上班时间_不算应出勤也不算未打卡()
    {
        Seed();
        using var db = CreateContext();
        var svc = Svc(db, 10);
        var s = await svc.GetTodayStatsAsync();

        Assert.Equal(4, s.TotalEmployees);
        Assert.Equal(2, s.PresentCount);                  // D1、X
        Assert.Equal(3, s.ExpectedCount);                 // D1、X（已出勤）+ D2（白班已开始、没来）；夜班的 N1 还没到 20:00
        Assert.Equal(1, s.NotStartedCount);
        Assert.Equal(66.7, s.AttendanceRate);             // 2 / 3，而不是 2 / 4
        Assert.Equal(1, s.NotPunchedCount);               // 只有 D2
        var list = await svc.GetTodayStatsDetailAsync("notpunched");
        Assert.Equal(["D2"], list.Select(r => r.EmployeeNo).ToList());    // 卡片数字 = 下钻名单
        Assert.Equal(3, (await svc.GetRecentTrendAsync(null, 1))[0].ExpectedCount);   // 近 7 天里今天这一行同口径
    }

    [Fact]
    public async Task 晚上九点_夜班到了上班时间_没打卡就拉低出勤率_也进未打卡()
    {
        Seed();
        using var db = CreateContext();
        var svc = Svc(db, 21);
        var s = await svc.GetTodayStatsAsync();

        Assert.Equal(4, s.ExpectedCount);                 // N1 也算应出勤了
        Assert.Equal(0, s.NotStartedCount);
        Assert.Equal(50.0, s.AttendanceRate);             // 2 / 4
        Assert.Equal(2, s.NotPunchedCount);               // D2、N1
        Assert.Equal(["D2", "N1"], (await svc.GetTodayStatsDetailAsync("notpunched")).Select(r => r.EmployeeNo).OrderBy(n => n).ToList());
    }

    [Fact]
    public async Task 夜班人员提前打了卡_算出勤_不管有没有到点()
    {
        Seed();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = _n1, WorkDate = Tue, ClockInTime = Tue.ToDateTime(new TimeOnly(9, 30)), AttendanceStatus = AttendanceStatus.Normal });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var s = await Svc(db2, 10).GetTodayStatsAsync();
        Assert.Equal(3, s.PresentCount);
        Assert.Equal(4, s.ExpectedCount);
        Assert.Equal(0, s.NotStartedCount);
    }

    [Fact]
    public async Task 各考勤组和各班次出勤率_逐人判断后按组按班次汇总_没排班的只数出勤不给率()
    {
        Seed();
        using var db = CreateContext();
        var s = await Svc(db, 10).GetTodayStatsAsync();

        var day = Assert.Single(s.ByGroup, r => r.Name == "白班组");
        Assert.Equal((2, 2, 1), (day.Total, day.Expected, day.Present));
        Assert.Equal(50.0, day.Rate);
        var night = Assert.Single(s.ByGroup, r => r.Name == "夜班组");
        Assert.Equal((1, 0, 0, 1), (night.Total, night.Expected, night.Present, night.NotStarted));
        Assert.Null(night.Rate);                                          // 应出勤 0，没有出勤率
        var none = Assert.Single(s.ByGroup, r => r.Name == "未分组");
        Assert.Equal(1, none.Present);

        Assert.Equal(3, s.ByShift.Count);
        var dayShift = Assert.Single(s.ByShift, r => r.Name.Contains("白班组 · 白班"));
        Assert.Equal(50.0, dayShift.Rate);
        Assert.Contains("08:30-17:30", dayShift.Name);
        var nightShift = Assert.Single(s.ByShift, r => r.Name.Contains("夜班组 · 夜班"));
        Assert.Equal(1, nightShift.NotStarted);
        var unscheduled = Assert.Single(s.ByShift, r => r.IsUnscheduled);
        Assert.Equal(1, unscheduled.Present);
        Assert.Null(unscheduled.Rate);                                    // 没排班没法判断该不该来，只数出勤
    }

    [Fact]
    public async Task 过去的日子不看当前时间_近7天里昨天的夜班照常算应出勤()
    {
        Seed();
        using var db = CreateContext();
        // 时钟定在周三上午十点，周二（昨天）的夜班 N1 没来：整天算，应出勤 = D1、X、D2、N1
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance, new FixedClock(Tue.AddDays(1).ToDateTime(new TimeOnly(10, 0))));
        var trend = await svc.GetRecentTrendAsync(null, 2);
        Assert.Equal(Tue, trend[1].Date);
        Assert.Equal(4, trend[1].ExpectedCount);
    }
}
