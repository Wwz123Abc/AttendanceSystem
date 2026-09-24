using System.Reflection;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.BackgroundServices;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-09-24 月度报表核对后的三条规则（都是用户拍板的）：
/// ① 免考勤的账号不自动记旷工/未打卡，报表也不统计他们的旷工；
/// ② 设备同步：当天第一次打卡如果晚于班次下班时间，按"下班卡"处理（缺上班卡），不再当成上班卡算出几百分钟迟到；
/// ③ 后台：只有下班卡没有上班卡 → "未打卡（缺上班卡）"，不算旷工。
/// </summary>
public class AbsentAndPunchRuleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());
    private static readonly DateOnly Tue = new(2026, 9, 8);   // 周二，普通工作日

    public AbsentAndPunchRuleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    // ── ② 设备同步：首次打卡晚于下班时间 → 下班卡 ──────────────────────────

    private static ShiftSchedule DayShift(bool cross = false) => new()
    {
        ShiftName = "白班", WorkStartTime = new TimeOnly(8, 30), WorkEndTime = new TimeOnly(17, 30),
        LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, IsCrossDay = cross, StandardWorkHours = 8
    };

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
    public async Task 模板汇总表_免考勤的人旷工天数为0_普通人照常统计()
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

        Assert.Equal(0, report.Rows.Single(r => r.EmployeeNo == "A2").AbsentDays);
        Assert.Equal(3, report.Rows.Single(r => r.EmployeeNo == "N3").AbsentDays);
    }
}
