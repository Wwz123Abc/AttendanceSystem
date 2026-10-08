using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Implementations;
using Microsoft.Extensions.Logging.Abstractions;

namespace AttendanceSystem.Tests;

/// <summary>
/// 发薪考勤汇总表（模板月度汇总）的工时口径：每天工时 = 正班 + 加班；正班休息日一律 0、工作日最多算班次标准工时
/// （没排班按 8 小时）；实际总工时 = 正班 + 加班，直接按它发工资，不会把同一段在岗时间算两遍。
/// </summary>
public class PayrollReportHoursTests : SqliteTestBase
{
    private static AttendanceRecord Rec(int uid, DateOnly day, decimal wh, decimal ot) => new()
    {
        UserId = uid, WorkDate = day, ActualWorkHours = wh, OvertimeHours = ot,
        ClockInTime = day.ToDateTime(new TimeOnly(8, 24)), ClockOutTime = day.ToDateTime(new TimeOnly(22, 0)),
        AttendanceStatus = AttendanceSystem.Models.Enums.AttendanceStatus.Normal,
    };

    [Fact]
    public async Task 有排班的人_每天工时是正班加加班_休息日旧记录里的正班不再计入()
    {
        var (uid, _) = SeedWeekWorld();    // 周五、周六（休息日）、周日（休息日）、周一都有排班
        using (var db = CreateContext())
        {
            db.AttendanceRecords.AddRange(
                Rec(uid, Fri, 8, 4),        // 工作日：正班 8 + 加班 4
                Rec(uid, Sat, 8, 13.5m),    // 休息日：9/28 前的旧记录既记了正班 8 又记了加班 13.5，正班不计
                Rec(uid, Mon, 8.4m, 0));    // 工作日：8.4 向下取半小时 = 8
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var report = await svc.GenerateTemplateReportAsync(Fri, Mon, null);
        var row = report.Rows.Single(r => r.EmployeeNo == "L1");

        Assert.Equal(12m, row.DailyHours[0]);        // 周五 8 + 4
        Assert.Equal(13.5m, row.DailyHours[1]);      // 周六 只有加班
        Assert.Null(row.DailyHours[2]);              // 周日没有记录
        Assert.Equal(8m, row.DailyHours[3]);         // 周一
        Assert.Equal(16m, row.RegularWorkHours);     // 8 + 0 + 8
        Assert.Equal(17.5m, row.TotalOvertimeHours); // 4 + 13.5
        Assert.Equal(33.5m, row.PayableHours);       // 16 + 17.5
        Assert.Equal(row.RegularWorkHours + row.TotalOvertimeHours, row.PayableHours);
    }

    [Fact]
    public async Task 没排班的人_工作日正班按8小时封顶_晚上的加班不再被算两遍()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("N1", "没排班的人");
            db.Users.Add(u); db.SaveChanges(); uid = u.Id;
            db.AttendanceRecords.AddRange(
                Rec(uid, Fri, 12.1m, 4),    // 08:24~22:00 在岗，系统记的正班 12.1（没封顶），加班单 4 小时
                Rec(uid, Sat, 0, 8));       // 周末：只有加班
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var row = (await svc.GenerateTemplateReportAsync(Fri, Mon, null)).Rows.Single(r => r.EmployeeNo == "N1");

        Assert.Equal(12m, row.DailyHours[0]);       // 8（封顶）+ 4，不是 12 + 4 = 16
        Assert.Equal(8m, row.DailyHours[1]);
        Assert.Equal(8m, row.RegularWorkHours);
        Assert.Equal(12m, row.TotalOvertimeHours);
        Assert.Equal(20m, row.PayableHours);
    }

    [Fact]
    public async Task 员工页面的总工时_和发薪汇总表里这个人的实际总工时完全一致()
    {
        var (uid, _) = SeedWeekWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.AddRange(Rec(uid, Fri, 8, 4), Rec(uid, Sat, 8, 13.5m), Rec(uid, Mon, 8.4m, 0));
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var report = await svc.GenerateTemplateReportAsync(Fri, Mon, null);
        var mine = await svc.GetMyPayrollRowAsync(uid, Fri, Mon);

        Assert.NotNull(mine);
        Assert.Equal(report.Rows.Single(r => r.EmployeeNo == "L1").PayableHours, mine!.PayableHours);
        Assert.Equal(33.5m, mine.PayableHours);
    }

    [Fact]
    public async Task 免考勤账号没有发薪汇总行()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("X1", "管理员甲");
            u.Role = AttendanceSystem.Models.Enums.UserRole.Admin;
            db.Users.Add(u); db.SaveChanges(); uid = u.Id;
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        Assert.Null(await svc.GetMyPayrollRowAsync(uid, Fri, Mon));
    }
}
