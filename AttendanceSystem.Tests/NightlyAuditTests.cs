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

/// <summary>每晚自动数据核对（2026-10-22 新增）：工时被改乱自动改回，没法自动改的疑点通知管理员，没有疑点不打扰。</summary>
public class NightlyAuditTests : SqliteTestBase
{
    private async Task RunNightlyAuditAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AttendanceDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton(AppOptions);
        services.AddLogging();
        services.AddScoped<IAttendanceService>(sp => new AttendanceService(
            sp.GetRequiredService<AttendanceDbContext>(), AppOptions, NullLogger<AttendanceService>.Instance,
            new FixedClock(Tue.AddDays(1).ToDateTime(new TimeOnly(5, 0)))));
        using var provider = services.BuildServiceProvider();
        var svc = new AttendanceBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AttendanceBackgroundService>.Instance, new FixedClock(Tue.AddDays(1).ToDateTime(new TimeOnly(5, 0))));
        var method = typeof(AttendanceBackgroundService).GetMethod("RunNightlyAuditAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [])!;
    }

    private async Task SyncAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await sync.ProcessAttLogAsync("SNM", times.Select(t => new ZKAttLogRow("M1", t, 0, 15)).ToList());
    }

    private int AddAdmin()
    {
        using var db = CreateContext();
        var admin = U("ADM1", "管理员甲");
        admin.Role = UserRole.Admin;
        db.Users.Add(admin);
        db.SaveChanges();
        return admin.Id;
    }

    [Fact]
    public async Task 工时被改乱_每晚核对自动改回_没有剩余疑点就不通知()
    {
        var adminId = AddAdmin();
        var (uid, _) = SeedDayMidWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 25)), Tue.ToDateTime(new TimeOnly(12, 20)), Tue.ToDateTime(new TimeOnly(17, 35)));
        using (var db = CreateContext())
        {
            (await db.AttendanceRecords.SingleAsync(r => r.UserId == uid)).ActualWorkHours = 12m;
            await db.SaveChangesAsync();
        }

        await RunNightlyAuditAsync();

        using var check = CreateContext();
        Assert.Equal(8m, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid)).ActualWorkHours);
        Assert.False(await check.Notifications.AnyAsync(n => n.UserId == adminId && n.NotificationType == NotificationTypes.DataAudit));
    }

    [Fact]
    public async Task 有打卡没排班_没法自动改_通知管理员_重复跑只留最新一条未读()
    {
        var adminId = AddAdmin();
        var (uid, _) = SeedDayMidWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 25)), Tue.ToDateTime(new TimeOnly(12, 20)), Tue.ToDateTime(new TimeOnly(17, 35)));
        using (var db = CreateContext())
        {
            db.ShiftAssignments.RemoveRange(db.ShiftAssignments.Where(a => a.UserId == uid));   // 排班被删：有打卡没排班
            await db.SaveChangesAsync();
        }

        await RunNightlyAuditAsync();
        await RunNightlyAuditAsync();

        using var check = CreateContext();
        var unread = await check.Notifications.Where(n => n.UserId == adminId && n.NotificationType == NotificationTypes.DataAudit && !n.IsRead).ToListAsync();
        var one = Assert.Single(unread);
        Assert.Contains("有打卡但没排班", one.Content);
        Assert.False(await check.Notifications.AnyAsync(n => n.UserId == uid && n.NotificationType == NotificationTypes.DataAudit));   // 员工不会收到
    }

    [Fact]
    public async Task 深圳GA考勤组的没排班_不进通知()
    {
        var adminId = AddAdmin();
        var (uid, _) = SeedDayMidWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 25)), Tue.ToDateTime(new TimeOnly(12, 20)), Tue.ToDateTime(new TimeOnly(17, 35)));
        using (var db = CreateContext())
        {
            db.ShiftAssignments.RemoveRange(db.ShiftAssignments.Where(a => a.UserId == uid));
            var group = new AttendanceGroup { GroupName = "深圳GA考勤组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            (await db.Users.SingleAsync(u => u.Id == uid)).AttendanceGroupId = group.Id;
            await db.SaveChangesAsync();
        }

        await RunNightlyAuditAsync();

        using var check = CreateContext();
        Assert.False(await check.Notifications.AnyAsync(n => n.UserId == adminId && n.NotificationType == NotificationTypes.DataAudit));
    }
}
