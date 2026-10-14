using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>打卡时间表里的"实际工时 / 发薪日工时"必须和发薪汇总表每日格子是同一个数。</summary>
public class ClockTimeSheetPayrollMatchTests : SqliteTestBase
{
    private static AttendanceRecord Rec(int uid, DateOnly d, decimal hours, decimal ot) => new()
    {
        UserId = uid, WorkDate = d, ClockInTime = d.ToDateTime(new TimeOnly(8, 25)), ClockOutTime = d.ToDateTime(new TimeOnly(22, 0)),
        AttendanceStatus = AttendanceStatus.Normal, ActualWorkHours = hours, OvertimeHours = ot
    };

    [Fact]
    public async Task 在岗工时超过标准工时_或休息日有工时_打卡时间表和发薪表每日格子一致()
    {
        var (uid, _) = SeedWeekWorld();   // 白班标准 8 小时，周六周日是班次的休息日
        using (var db = CreateContext())
        {
            db.AttendanceRecords.AddRange(
                Rec(uid, Fri, 12m, 0),      // 在岗 12 小时、没有加班单：发薪只认标准 8 小时
                Rec(uid, Sat, 13.5m, 2),    // 休息日：正班 0，只算加班 2
                Rec(uid, Mon, 8m, 2.5m));   // 正常 8 + 加班 2.5
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var sheet = (await svc.GetClockTimeSheetAsync(Fri, Mon, null)).ToDictionary(r => r.WorkDate);
        var payroll = await svc.GetMyPayrollRowAsync(uid, Fri, Mon);

        Assert.Equal(8m, sheet[Fri].PayrollRegularHours);
        Assert.Equal(0m, sheet[Sat].PayrollRegularHours);
        Assert.Equal(2m, sheet[Sat].PayrollDayHours);
        Assert.Equal(10.5m, sheet[Mon].PayrollDayHours);

        // 和发薪表的每日格子逐天对上（发薪表日期顺序：Fri, Sat, Sun, Mon）
        var dates = new[] { Fri, Sat, Sun, Mon };
        for (var i = 0; i < dates.Length; i++)
        {
            var cell = payroll!.DailyHours[i];
            if (sheet.TryGetValue(dates[i], out var r)) Assert.Equal(cell ?? 0m, r.PayrollDayHours ?? 0m);
        }
    }
}
