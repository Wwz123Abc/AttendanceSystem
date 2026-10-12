using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Exceptions;

namespace AttendanceSystem.Tests;

/// <summary>
/// 看板与月度汇总：未打卡人数不重复扣、未来补卡、汇总并发重试、手动重算的范围。
/// </summary>
public class DashboardAndSummaryTests : SqliteTestBase
{
    // ── M1：看板"未打卡"人数不能把"半天假且打了上班卡"的人扣两次 ─────────────────────

    [Fact]
    public async Task 看板_半天假打了上班卡的人_未打卡人数仍等于下钻名单条数_不会被扣两次()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        using (var db = CreateContext())
        {
            var halfDay = U("H1", "半天假员工");   // 上午上班、下午请假：既算"出勤"又算"请假"
            var absent  = U("H2", "旷工员工");
            var none    = U("H3", "还没来的员工");
            db.Users.AddRange(halfDay, absent, none);
            await db.SaveChangesAsync();
            db.AttendanceRecords.AddRange(
                new AttendanceRecord { UserId = halfDay.Id, WorkDate = today, ClockInTime = today.ToDateTime(new TimeOnly(8, 30)), AttendanceStatus = AttendanceStatus.OnLeave, LeaveHours = 4 },
                new AttendanceRecord { UserId = absent.Id, WorkDate = today, AttendanceStatus = AttendanceStatus.Absent });
            await db.SaveChangesAsync();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var stats = await svc.GetTodayStatsAsync();
        var list = await svc.GetTodayStatsDetailAsync("notpunched");

        Assert.Equal(3, stats.TotalEmployees);
        Assert.Equal(1, stats.PresentCount);
        Assert.Equal(1, stats.NotPunchedCount);                 // 只有"还没来的员工"；以前被少算成 0
        Assert.Equal(stats.NotPunchedCount, list.Count);        // 卡片数字 = 下钻名单人数
    }

    // ── 看板出勤率：分母是"应出勤"，不是在职总人数 ──────────────────────────

    /// <summary>造一批典型的人：返回的 today/yesterday 两天的数据用于下面两条测试。</summary>
    private async Task<(DateOnly today, DateOnly yesterday)> SeedAttendanceRateWorld()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "组" };
        db.AttendanceGroups.Add(group);
        await db.SaveChangesAsync();
        var work = DayShift(); work.AttendanceGroupId = group.Id; work.RestDaysOfWeek = "";                      // 每天都是工作日
        var rest = DayShift(); rest.AttendanceGroupId = group.Id; rest.RestDaysOfWeek = "0,1,2,3,4,5,6";       // 每天都是休息日
        db.ShiftSchedules.AddRange(work, rest);
        var a = U("A", "准点");   var b = U("B", "迟到");     var c = U("C", "还没来");   var d = U("D", "今天休息");
        var e = U("E", "请假");   var f = U("F", "没排班没来"); var g = U("G", "没排班但来了"); var h = U("H", "明天才入职");
        var i = U("I", "没填入职日期");
        h.HireDate = today.AddDays(1); i.HireDate = null;
        db.Users.AddRange(a, b, c, d, e, f, g, h, i);
        await db.SaveChangesAsync();
        foreach (var u in new[] { a, b, c, e, h }) foreach (var day in new[] { today, yesterday })
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = u.Id, WorkDate = day, ShiftScheduleId = work.Id });
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = d.Id, WorkDate = today, ShiftScheduleId = rest.Id });
        db.AttendanceRecords.AddRange(
            new AttendanceRecord { UserId = a.Id, WorkDate = today, ClockInTime = today.ToDateTime(new TimeOnly(8, 0)), AttendanceStatus = AttendanceStatus.Normal },
            new AttendanceRecord { UserId = b.Id, WorkDate = today, ClockInTime = today.ToDateTime(new TimeOnly(9, 40)), AttendanceStatus = AttendanceStatus.Late },
            new AttendanceRecord { UserId = e.Id, WorkDate = today, AttendanceStatus = AttendanceStatus.OnLeave },
            new AttendanceRecord { UserId = g.Id, WorkDate = today, ClockInTime = today.ToDateTime(new TimeOnly(8, 10)), AttendanceStatus = AttendanceStatus.Normal },
            // 昨天：A 正常，B 旷工，C 旷工，E 请假
            new AttendanceRecord { UserId = a.Id, WorkDate = yesterday, ClockInTime = yesterday.ToDateTime(new TimeOnly(8, 0)), AttendanceStatus = AttendanceStatus.Normal },
            new AttendanceRecord { UserId = b.Id, WorkDate = yesterday, AttendanceStatus = AttendanceStatus.Absent },
            new AttendanceRecord { UserId = c.Id, WorkDate = yesterday, AttendanceStatus = AttendanceStatus.Absent },
            new AttendanceRecord { UserId = e.Id, WorkDate = yesterday, AttendanceStatus = AttendanceStatus.OnLeave });
        await db.SaveChangesAsync();
        return (today, yesterday);
    }

    [Fact]
    public async Task 看板出勤率_分母是应出勤_没排班休息日没入职请假的人没来不拉低_来了照样算()
    {
        await SeedAttendanceRateWorld();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var s = await svc.GetTodayStatsAsync();

        Assert.Equal(9, s.TotalEmployees);                 // 总人数卡片仍是全部在职需考勤的人
        Assert.Equal(3, s.PresentCount);                   // A、B（迟到也算出勤）、G（没排班但来了）
        // 应出勤 = A、B、G（已出勤） + C（排了班、还没来）= 4；
        // D 今天休息、E 请假、F 没排班没来、H 明天才入职、I 没填入职日期，都不算
        Assert.Equal(4, s.ExpectedCount);
        Assert.Equal(75.0, s.AttendanceRate);              // 3 / 4，而不是 3 / 9 = 33.3%
    }

    [Fact]
    public async Task 看板近7天_每天按各自的应出勤算出勤率_没有记录的日子标成无数据_昨日旷工能看到()
    {
        var (today, yesterday) = await SeedAttendanceRateWorld();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var trend = await svc.GetRecentTrendAsync(null, 7);

        Assert.Equal(7, trend.Count);
        Assert.Equal(today, trend[0].Date);               // 最新的一天排最前
        Assert.Equal(today.AddDays(-6), trend[^1].Date);

        Assert.True(trend[0].HasData);
        Assert.Equal(3, trend[0].PresentCount);
        Assert.Equal(1, trend[0].LateCount);
        Assert.Equal(1, trend[0].OnLeaveCount);
        Assert.Equal(4, trend[0].ExpectedCount);          // 同今日卡片
        Assert.Equal(75.0, trend[0].AttendanceRate);

        Assert.Equal(yesterday, trend[1].Date);
        Assert.Equal(1, trend[1].PresentCount);           // A
        Assert.Equal(2, trend[1].AbsentCount);            // B、C 昨天旷工
        Assert.Equal(1, trend[1].OnLeaveCount);
        Assert.Equal(3, trend[1].ExpectedCount);          // A + B + C（E 请假不算；G 昨天没来也没排班不算）
        Assert.Equal(33.3, trend[1].AttendanceRate);      // 1 / 3

        Assert.False(trend[2].HasData);                   // 前天没有任何记录 = 休息日/节假日
        Assert.Equal(0, trend[2].AttendanceRate);
    }

    // ── M11：管理员手动补卡不能补未来的时间点 ───────────────────────────────────────

    [Fact]
    public async Task 管理员手动补卡_未来的时间点被拒绝_过去的正常补()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("P1", "补卡员工");
            db.Users.Add(u);
            await db.SaveChangesAsync();
            uid = u.Id;
        }
        var today = DateOnly.FromDateTime(DateTime.Now);
        var yesterday = today.AddDays(-1);

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => svc.AdminAdjustPunchAsync(uid, today, null, DateTime.Now.AddHours(3), null, "管理员"));
        Assert.Contains("还没到", ex.Message);

        await svc.AdminAdjustPunchAsync(uid, yesterday, yesterday.ToDateTime(new TimeOnly(9, 0)), yesterday.ToDateTime(new TimeOnly(18, 0)), null, "管理员");
        using var check = CreateContext();
        Assert.True(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == yesterday && r.ClockOutTime != null));
    }

    // ── M9：月度汇总插入撞唯一索引时自动重试一次，不能整批回滚、当月汇总静默缺失 ──────────────

    private sealed class ConflictOnFirstSave(SqliteConnection conn, int userId, int year, int month) : SaveChangesInterceptor
    {
        private bool _done;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!_done)
            {
                _done = true;   // 模拟"另一个进程在我查完、还没保存之前，抢先把这个人这个月的汇总插进去了"
                using var other = new AttendanceDbContext(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(conn).Options);
                other.MonthlyAttendanceSummaries.Add(new MonthlyAttendanceSummary { UserId = userId, Year = year, Month = month });
                await other.SaveChangesAsync(ct);
            }
            return result;
        }
    }

    [Fact]
    public async Task 月度汇总_保存时撞上别人刚插入的同一条_自动重试_不抛异常_只有一条汇总()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("S1", "汇总员工");
            db.Users.Add(u);
            await db.SaveChangesAsync();
            uid = u.Id;
        }

        var options = new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection)
            .AddInterceptors(new ConflictOnFirstSave(_connection, uid, 2026, 9)).Options;
        using (var db2 = new AttendanceDbContext(options))
        {
            var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.GenerateMonthlySummaryAsync(2026, 9, [uid]);   // 以前这里会抛 DbUpdateException
        }

        using var check = CreateContext();
        Assert.Equal(1, await check.MonthlyAttendanceSummaries.CountAsync(s => s.UserId == uid && s.Year == 2026 && s.Month == 9));
    }

    // ── 手动重算月度汇总：空名单 ≠ 全公司 ──────────────────────────────────

    [Fact]
    public async Task 月度汇总_传空名单不做任何重算_不传才是全公司()
    {
        using (var seed = CreateContext())
        {
            seed.Users.AddRange(
                new User { EmployeeNo = "U1", RealName = "甲", PasswordHash = "x", IsActive = true },
                new User { EmployeeNo = "U2", RealName = "乙", PasswordHash = "x", IsActive = true });
            seed.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.GenerateMonthlySummaryAsync(2026, 8, []);   // 空名单（控制器对"没设范围的非 Admin 账号"传的就是这个）
        }
        using (var check = CreateContext())
            Assert.Empty(await check.MonthlyAttendanceSummaries.ToListAsync());

        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.GenerateMonthlySummaryAsync(2026, 8);       // 不传 = 全公司（总部 Admin 的合法用法）
        }
        using (var check = CreateContext())
            Assert.Equal(2, await check.MonthlyAttendanceSummaries.CountAsync());
    }
}
