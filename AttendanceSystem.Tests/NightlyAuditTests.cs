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
    public async Task 结算时就有班次冻结了窗口的记录_工时被改乱_不自动改_只通知管理员()
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
        Assert.Equal(12m, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid)).ActualWorkHours);   // 记录上有冻结的午间结果：夜里不动它
        var notice = await check.Notifications.SingleAsync(n => n.UserId == adminId && n.NotificationType == NotificationTypes.DataAudit);
        Assert.Contains("工时与打卡不符", notice.Content);
    }

    [Fact]
    public async Task 结算时还没排班_后来补排了班_每晚核对自动重算工时()
    {
        var adminId = AddAdmin();
        var (uid, shiftId) = SeedDayMidWorld();
        using (var db = CreateContext())   // 先把排班删掉：打卡结算时没有班次
        {
            db.ShiftAssignments.RemoveRange(db.ShiftAssignments.Where(a => a.UserId == uid));
            await db.SaveChangesAsync();
        }
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 25)), Tue.ToDateTime(new TimeOnly(12, 20)), Tue.ToDateTime(new TimeOnly(18, 35)));
        decimal before;
        using (var db = CreateContext())   // 后来补排了班
        {
            before = (await db.AttendanceRecords.SingleAsync(r => r.UserId == uid)).ActualWorkHours;
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = uid, WorkDate = Tue, ShiftScheduleId = shiftId });
            await db.SaveChangesAsync();
        }
        Assert.True(before > 8m);   // 没班次时不封顶：晚走的钟点都算进去了

        await RunNightlyAuditAsync();

        using var check = CreateContext();
        Assert.Equal(8m, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid)).ActualWorkHours);   // 按班次重算：封顶到下班时间
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

    [Fact]
    public async Task 数据核对本身就忽略深圳GA考勤组的没排班_别的考勤组照常报()
    {
        var (uid, _) = SeedDayMidWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 25)), Tue.ToDateTime(new TimeOnly(12, 20)), Tue.ToDateTime(new TimeOnly(17, 35)));
        int groupId;
        using (var db = CreateContext())
        {
            db.ShiftAssignments.RemoveRange(db.ShiftAssignments.Where(a => a.UserId == uid));
            await db.SaveChangesAsync();
            var g = new AttendanceGroup { GroupName = "深圳GA考勤组" };
            db.AttendanceGroups.Add(g); await db.SaveChangesAsync(); groupId = g.Id;
        }
        using (var db = CreateContext())
            Assert.Contains(await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).AuditAsync(Tue, Tue), i => i.Type == AttendanceService.AuditNoShift);   // 现在在"午卡组"：照常报
        using (var db = CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == uid)).AttendanceGroupId = groupId;
            await db.SaveChangesAsync();
        }
        using (var db = CreateContext())
            Assert.DoesNotContain(await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).AuditAsync(Tue, Tue), i => i.Type == AttendanceService.AuditNoShift);   // 换到深圳GA：忽略
    }
}
