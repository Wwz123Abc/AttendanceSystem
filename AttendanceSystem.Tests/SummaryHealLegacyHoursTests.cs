using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 月度汇总生成不再产生"读请求写库"的副作用（2026-11-06 审计 R2）：
/// 只读路径（如"导出某人明细"的 GET）只按最新规则算出合计，不把补算结果写回考勤记录；
/// 写路径（打卡/审批/后台任务）仍然会把老数据补算结果写回，让老数据自愈。
/// </summary>
public class SummaryHealLegacyHoursTests : SqliteTestBase
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>造一条"有上下班卡但工时还是 0"的老记录（模拟历史遗留），排今天的白班。</summary>
    private (int uid, int year, int month) SeedLegacyRecord()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "自愈测试组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var shift = DayShift();                       // 08:30–17:30，标准工时 8
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);

        var user = U("SHF1", "自愈测试");
        user.AttendanceGroupId = group.Id;
        user.HireDate = new DateOnly(Today.Year, 1, 1);
        db.Users.Add(user);
        db.SaveChanges();

        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = Today, ShiftScheduleId = shift.Id });
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            UserId = user.Id, WorkDate = Today,
            ClockInTime  = Today.ToDateTime(new TimeOnly(8, 30)),
            ClockOutTime = Today.ToDateTime(new TimeOnly(17, 30)),
            AttendanceStatus = AttendanceStatus.Normal,
            ActualWorkHours = 0
        });
        db.SaveChanges();
        return (user.Id, Today.Year, Today.Month);
    }

    [Fact]
    public async Task 只读生成汇总_不写回考勤记录()
    {
        var (uid, year, month) = SeedLegacyRecord();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);

        await svc.GenerateMonthlySummaryAsync(year, month, [uid], healLegacyHours: false);

        db.ChangeTracker.Clear();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Today);
        Assert.Equal(0m, rec.ActualWorkHours);        // 记录没被改（读请求无写副作用）

        var sum = await db.MonthlyAttendanceSummaries.SingleAsync(s => s.UserId == uid && s.Year == year && s.Month == month);
        Assert.Equal(8m, sum.TotalWorkHours);         // 但合计仍按最新规则算出来了
    }

    [Fact]
    public async Task 写路径生成汇总_会把补算结果写回记录()
    {
        var (uid, year, month) = SeedLegacyRecord();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);

        await svc.GenerateMonthlySummaryAsync(year, month, [uid]);   // 默认 healLegacyHours: true

        db.ChangeTracker.Clear();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Today);
        Assert.Equal(8m, rec.ActualWorkHours);        // 老数据被自愈写回
    }
}
