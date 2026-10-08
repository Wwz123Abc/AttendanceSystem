using System.Reflection;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.BackgroundServices;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 旷工与打卡规则：设备同步首次打卡晚于下班时间、后台记旷工、免考勤不统计旷工/应出勤。
/// </summary>
public class AbsentAndPunchRuleTests : SqliteTestBase
{
    // ── ② 设备同步：首次打卡晚于下班时间 → 下班卡 ──────────────────────────

    [Theory]
    [InlineData(22, 1, true)]     // 22:01：下班时间之后
    [InlineData(17, 30, true)]    // 正好 17:30：算下班时间之后
    [InlineData(17, 29, false)]
    [InlineData(8, 22, false)]    // 正常早上打卡
    [InlineData(12, 57, false)]   // 中午到岗（迟到）仍然是上班卡
    public void 首次打卡是否晚于下班时间(int h, int m, bool expected)
        => Assert.Equal(expected, AttendanceService.IsFirstPunchAfterShiftEnd(Tue, Tue.ToDateTime(new TimeOnly(h, m)), DayShift(), false));

    [Fact]
    public void 夜班_没排班_休息日_都不做这个判断()
    {
        var late = Tue.ToDateTime(new TimeOnly(22, 0));
        Assert.False(AttendanceService.IsFirstPunchAfterShiftEnd(Tue, late, DayShift(cross: true), false));   // 夜班晚上首次打卡本来就是上班
        Assert.False(AttendanceService.IsFirstPunchAfterShiftEnd(Tue, late, null, false));
        Assert.False(AttendanceService.IsFirstPunchAfterShiftEnd(Tue, late, DayShift(), isRestDay: true));
    }

    private (int userId, int shiftId) SeedDeviceWorld(string sn = "SN1")
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "生产组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = DayShift(); shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "E1", RealName = "员工甲", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 9, 1) };
        db.Users.Add(user);
        var dev = new ZKDevice { SN = sn, IsActive = true };
        db.ZKDevices.Add(dev);
        db.SaveChanges();
        db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
        foreach (var d in new[] { Tue, Tue.AddDays(1) })
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return (user.Id, shift.Id);
    }

    private ZKDeviceSyncService SyncSvc(AttendanceDbContext db)
    {
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        return new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
    }

    [Fact]
    public async Task 设备同步_当天唯一一次打卡在22点_按下班卡处理_不算迟到()
    {
        var (uid, _) = SeedDeviceWorld();
        using (var db = CreateContext())
            await SyncSvc(db).ProcessAttLogAsync("SN1", [new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(22, 1)), 0, 15)]);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Null(rec.ClockInTime);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(22, 1)), rec.ClockOutTime);
        Assert.Equal(0, rec.LateMinutes);                                   // 以前这里是 811
        var punch = await check.AttendancePunches.SingleAsync(p => p.UserId == uid);
        Assert.Equal(PunchType.ClockOut, punch.PunchType);
    }

    [Fact]
    public async Task 设备同步_正常早晚两次打卡_仍然是上班加下班_迟到照算()
    {
        var (uid, _) = SeedDeviceWorld();
        using (var db = CreateContext())
            await SyncSvc(db).ProcessAttLogAsync("SN1", [
                new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(12, 57)), 0, 15),   // 中午才到：上班卡，迟到
                new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(21, 0)), 0, 15)]);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(12, 57)), rec.ClockInTime);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(21, 0)), rec.ClockOutTime);
        Assert.Equal(AttendanceStatus.Late, rec.AttendanceStatus);
        Assert.Equal(267, rec.LateMinutes);
    }

    // ── ①③ 后台记旷工 ────────────────────────────────────────────────────

    private async Task RunMarkAbsentAsync(DateOnly day)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AttendanceDbContext>(o => o.UseSqlite(_connection));
        using var provider = services.BuildServiceProvider();
        var svc = new AttendanceBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AttendanceBackgroundService>.Instance);
        var method = typeof(AttendanceBackgroundService).GetMethod("MarkAbsentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [day])!;
    }

    private static User U(string no, string name, bool exempt = false) => new()
    {
        EmployeeNo = no, RealName = name, PasswordHash = "x", IsActive = true,
        HireDate = new DateOnly(2026, 9, 1), IsAttendanceExempt = exempt
    };

    [Fact]
    public async Task 后台记旷工_免考勤的人不记_普通人照记并发提醒()
    {
        int exemptId, normalId;
        using (var db = CreateContext())
        {
            var exempt = U("A1", "管理员甲", exempt: true); var normal = U("N1", "员工乙");
            db.Users.AddRange(exempt, normal);
            db.SaveChanges();
            exemptId = exempt.Id; normalId = normal.Id;
        }

        await RunMarkAbsentAsync(Tue);

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == exemptId));                 // 免考勤：没有任何记录
        Assert.Equal(AttendanceStatus.Absent, (await check.AttendanceRecords.SingleAsync(r => r.UserId == normalId)).AttendanceStatus);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.UserId == normalId));
        Assert.Equal(0, await check.Notifications.CountAsync(n => n.UserId == exemptId));
    }

    [Fact]
    public async Task 后台记旷工_只有下班卡没有上班卡_记未打卡不记旷工_重复跑不重复提醒()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("N2", "员工丙");
            db.Users.Add(u); db.SaveChanges();
            uid = u.Id;
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = Tue, ClockOutTime = Tue.ToDateTime(new TimeOnly(22, 1)), AttendanceStatus = AttendanceStatus.Normal
            });
            db.SaveChanges();
        }

        await RunMarkAbsentAsync(Tue);
        await RunMarkAbsentAsync(Tue);   // 补跑/重启后重复执行

        using var check = CreateContext();
        Assert.Equal(AttendanceStatus.NotPunched, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid)).AttendanceStatus);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.UserId == uid));
    }

    // ── ① 报表不统计免考勤的旷工 ─────────────────────────────────────────

    [Fact]
    public async Task 模板汇总表_免考勤的人不进报表_普通人照常统计旷工()
    {
        using (var db = CreateContext())
        {
            var exempt = U("A2", "管理员乙", exempt: true); var normal = U("N3", "员工丁");
            db.Users.AddRange(exempt, normal);
            db.SaveChanges();
            foreach (var u in new[] { exempt, normal })
                for (var i = 0; i < 3; i++)
                    db.AttendanceRecords.Add(new AttendanceRecord { UserId = u.Id, WorkDate = Tue.AddDays(i), AttendanceStatus = AttendanceStatus.Absent });
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var report = await svc.GenerateTemplateReportAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), null);

        Assert.DoesNotContain(report.Rows, r => r.EmployeeNo == "A2");   // 免考勤的人不进报表（2026-10-07 起整行都不显示）
        Assert.Equal(3, report.Rows.Single(r => r.EmployeeNo == "N3").AbsentDays);
    }

    // ── 2026-09-24 审查补漏：④ 下班卡晚到不再停在旷工；⑤ 免考勤进看板口径、应出勤天数为 0 ─────────

    [Fact]
    public async Task 设备同步_23点55已被标旷工_之后补传的下班卡_旷工改成未打卡()
    {
        var (uid, _) = SeedDeviceWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, AttendanceStatus = AttendanceStatus.Absent });
            db.SaveChanges();
        }
        using (var db = CreateContext())
            await SyncSvc(db).ProcessAttLogAsync("SN1", [new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(22, 1)), 0, 15)]);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Null(rec.ClockInTime);
        Assert.Equal(AttendanceStatus.NotPunched, rec.AttendanceStatus);   // 以前一直停在"旷工"
    }

    [Fact]
    public async Task 设备同步_旷工的人补传了上班卡_照旧被纠正回来_不受影响()
    {
        var (uid, _) = SeedDeviceWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, AttendanceStatus = AttendanceStatus.Absent });
            db.SaveChanges();
        }
        using (var db = CreateContext())
            await SyncSvc(db).ProcessAttLogAsync("SN1", [new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(8, 25)), 0, 15)]);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(AttendanceStatus.Normal, rec.AttendanceStatus);
    }

    [Fact]
    public async Task 看板_免考勤的人不进总人数和未打卡名单()
    {
        int normalId;
        using (var db = CreateContext())
        {
            var exempt = U("A3", "管理员丙", exempt: true); var normal = U("N4", "员工戊");
            db.Users.AddRange(exempt, normal);
            db.SaveChanges();
            normalId = normal.Id;
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var stats = await svc.GetTodayStatsAsync();
        Assert.Equal(1, stats.TotalEmployees);
        Assert.Equal(1, stats.NotPunchedCount);

        var list = await svc.GetTodayStatsDetailAsync("notpunched");
        Assert.Equal([normalId], list.Select(r => r.UserId).ToList());          // 名单里只有需要打卡的人，且条数 = 卡片数字
        Assert.Equal(stats.NotPunchedCount, list.Count);
        Assert.Single(await svc.GetTodayStatsDetailAsync("total"));
    }

    [Fact]
    public async Task 应出勤天数_免考勤的人不进报表_普通人照常算_模板表和月度汇总一致()
    {
        using (var db = CreateContext())
        {
            db.Users.AddRange(U("A4", "管理员丁", exempt: true), U("N5", "员工己"));
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var report = await svc.GenerateTemplateReportAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), null);
        Assert.DoesNotContain(report.Rows, r => r.EmployeeNo == "A4");   // 免考勤的人不进报表
        Assert.True(report.Rows.Single(r => r.EmployeeNo == "N5").ExpectedWorkdays > 0);

        await svc.GenerateMonthlySummaryAsync(2026, 9, null);
        using var check = CreateContext();
        var sums = await check.MonthlyAttendanceSummaries.Include(m => m.User).ToListAsync();
        Assert.DoesNotContain(sums, m => m.User.EmployeeNo == "A4");     // 也不生成月度汇总
        Assert.Equal(report.Rows.Single(r => r.EmployeeNo == "N5").ExpectedWorkdays, sums.Single(m => m.User.EmployeeNo == "N5").ExpectedWorkdays);
    }

    // ── 2026-10-07：只有"普通员工"角色需要考勤，管理员/文员/主管/班组长都是正式工，一律免考勤 ──────────

    private static readonly UserRole[] FormalRoles = [UserRole.Admin, UserRole.Clerk, UserRole.Supervisor, UserRole.TeamLeader];

    private int SeedFormalAndEmployee()
    {
        using var db = CreateContext();
        var employee = U("EMP", "临时工");
        var formal = FormalRoles.Select(r => { var u = U("F" + (int)r, "正式工" + r); u.Role = r; return u; }).ToList();
        db.Users.AddRange(formal);
        db.Users.Add(employee);
        db.SaveChanges();
        return employee.Id;
    }

    [Fact]
    public async Task 正式工角色_不记旷工_不发打卡提醒_不进看板()
    {
        var empId = SeedFormalAndEmployee();
        await RunMarkAbsentAsync(Tue);

        using var check = CreateContext();
        var recs = await check.AttendanceRecords.ToListAsync();
        Assert.Equal([empId], recs.Select(r => r.UserId).ToList());                 // 只有普通员工被记旷工
        Assert.Equal(1, await check.Notifications.CountAsync());                    // 也只给他发了提醒

        var svc = new AttendanceService(check, AppOptions, NullLogger<AttendanceService>.Instance);
        var stats = await svc.GetTodayStatsAsync();
        Assert.Equal(1, stats.TotalEmployees);                                      // 看板总人数只算需要考勤的人
        Assert.Equal([empId], (await svc.GetTodayStatsDetailAsync("total")).Select(r => r.UserId).ToList());
    }

    [Fact]
    public async Task 正式工角色_不进模板汇总表_月度汇总_打卡时间表_部门考勤记录()
    {
        var empId = SeedFormalAndEmployee();
        using (var db = CreateContext())
        {
            // 就算正式工自己打了卡/有记录，也不在报表里体现
            foreach (var u in db.Users.ToList())
                db.AttendanceRecords.Add(new AttendanceRecord
                {
                    UserId = u.Id, WorkDate = Tue, ClockInTime = Tue.ToDateTime(new TimeOnly(8, 30)),
                    ClockOutTime = Tue.ToDateTime(new TimeOnly(17, 30)), AttendanceStatus = AttendanceStatus.Normal, ActualWorkHours = 8
                });
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var start = new DateOnly(2026, 9, 1); var end = new DateOnly(2026, 9, 30);

        Assert.Equal(["EMP"], (await svc.GenerateTemplateReportAsync(start, end, null)).Rows.Select(r => r.EmployeeNo).ToList());
        Assert.Equal([empId], (await svc.GetClockTimeSheetAsync(start, end, null)).Select(r => r.UserId).Distinct().ToList());
        Assert.Equal([empId], (await svc.GetDeptAttendanceAsync(new AttendanceSystem.Models.DTOs.DeptAttendanceQueryDto { StartDate = start, EndDate = end })).Select(r => r.UserId).Distinct().ToList());

        await svc.GenerateMonthlySummaryAsync(2026, 9, null);
        Assert.Equal(["EMP"], (await svc.GetDeptMonthlySummariesAsync(null, null, 2026, 9)).Select(x => x.EmployeeNo).ToList());
    }

    [Fact]
    public void 免考勤判断_普通员工看开关_其他角色一律免考勤()
    {
        Assert.False(U("A", "员工").IsExemptFromAttendance());
        var flagged = U("B", "勾了免考勤的员工", exempt: true);
        Assert.True(flagged.IsExemptFromAttendance());
        foreach (var r in FormalRoles)
        {
            var u = U("C", "正式工"); u.Role = r;
            Assert.True(u.IsExemptFromAttendance());
        }
    }

    [Fact]
    public void 用工性质_只有普通员工是临时工_其余角色都是正式工()
    {
        foreach (var r in Enum.GetValues<UserRole>())
        {
            var u = U("T", "测试"); u.Role = r;
            Assert.Equal(r == UserRole.Employee ? "临时工" : "正式工", u.EmploymentTypeText);
            Assert.Equal(u.EmploymentTypeText == "正式工", u.Role != UserRole.Employee && u.IsExemptFromAttendance());   // 用工性质和免考勤同一口径
        }
    }
}
