using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-09-29：公告新增"按角色发布"（用户要求只发给管理员+文员，现有范围——全公司/部门/考勤组/直属下属——
/// 都覆盖不了这个需求）。这里只测服务层的受众计算和校验，跟"受限管理员不能选全公司"一样，
/// 页面层的角色限制（Publish.cshtml.cs）沿用现有惯例不做 PageModel 级别的测试。
/// </summary>
public class AnnouncementServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public AnnouncementServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    private int SeedUser(AttendanceDbContext db, string no, UserRole role)
    {
        var u = new User { EmployeeNo = no, RealName = no, PasswordHash = "x", IsActive = true, Role = role };
        db.Users.Add(u);
        db.SaveChanges();
        return u.Id;
    }

    [Fact]
    public async Task 按角色发布_只发给选中的角色()
    {
        int adminId, clerkId;
        using (var db = CreateContext())
        {
            adminId = SeedUser(db, "A1", UserRole.Admin);
            clerkId = SeedUser(db, "C1", UserRole.Clerk);
            SeedUser(db, "S1", UserRole.Supervisor);
            SeedUser(db, "E1", UserRole.Employee);
        }

        Announcement ann;
        using (var db = CreateContext())
        {
            var svc = new AnnouncementService(db);
            ann = await svc.PublishAsync(adminId, UserRole.Admin, new PublishAnnouncementDto
            {
                Title      = "智能助手上线通知",
                Content    = "测试阶段，请知悉",
                ScopeType  = AnnouncementScopeType.Role,
                ScopeRoles = [UserRole.Admin, UserRole.Clerk]
            });
        }

        using var check = CreateContext();
        var audience = await check.AnnouncementReads.Where(r => r.AnnouncementId == ann.Id)
            .Select(r => r.UserId).ToListAsync();
        Assert.Equal(2, audience.Count);
        Assert.Contains(adminId, audience);
        Assert.Contains(clerkId, audience);
    }

    [Fact]
    public async Task 按角色发布_一个角色都没选_报错()
    {
        int adminId;
        using (var db = CreateContext())
            adminId = SeedUser(db, "A1", UserRole.Admin);

        using var db2 = CreateContext();
        var svc = new AnnouncementService(db2);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishAsync(adminId, UserRole.Admin, new PublishAnnouncementDto
        {
            Title = "标题", Content = "内容", ScopeType = AnnouncementScopeType.Role, ScopeRoles = []
        }));
        Assert.Equal("请至少选择一个角色", ex.Message);
    }

    [Fact]
    public async Task 按角色发布_选中的角色目前没有在职员工_报错()
    {
        int adminId;
        using (var db = CreateContext())
            adminId = SeedUser(db, "A1", UserRole.Admin);

        using var db2 = CreateContext();
        var svc = new AnnouncementService(db2);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishAsync(adminId, UserRole.Admin, new PublishAnnouncementDto
        {
            Title = "标题", Content = "内容", ScopeType = AnnouncementScopeType.Role, ScopeRoles = [UserRole.TeamLeader]
        }));
        Assert.Equal("这个范围里没有任何在职员工，无法发布", ex.Message);
    }

    [Fact]
    public async Task 按角色发布_我发布的列表里显示角色中文名()
    {
        int adminId;
        using (var db = CreateContext())
        {
            adminId = SeedUser(db, "A1", UserRole.Admin);
            SeedUser(db, "C1", UserRole.Clerk);
        }

        using (var db = CreateContext())
        {
            var svc = new AnnouncementService(db);
            await svc.PublishAsync(adminId, UserRole.Admin, new PublishAnnouncementDto
            {
                Title = "标题", Content = "内容", ScopeType = AnnouncementScopeType.Role, ScopeRoles = [UserRole.Admin, UserRole.Clerk]
            });
        }

        using var check = CreateContext();
        var svc2 = new AnnouncementService(check);
        var mine = await svc2.GetMyPublishedAsync(adminId);
        var item = Assert.Single(mine);
        Assert.Equal("管理员、文员", item.ScopeText);
    }

    [Fact]
    public async Task 班组长发布公告_即使传了按角色范围_也会被强制锁死成直属下属()
    {
        int leaderId, reportId;
        using (var db = CreateContext())
        {
            leaderId = SeedUser(db, "L1", UserRole.TeamLeader);
            var report = new User { EmployeeNo = "R1", RealName = "R1", PasswordHash = "x", IsActive = true, Role = UserRole.Employee, SupervisorUserId = leaderId };
            db.Users.Add(report);
            db.SaveChanges();
            reportId = report.Id;
        }

        Announcement ann;
        using (var db = CreateContext())
        {
            var svc = new AnnouncementService(db);
            // 班组长不是 isManager（Admin/Clerk），即使传了 Role 范围，服务端也必须无视它、锁死成直属下属
            ann = await svc.PublishAsync(leaderId, UserRole.TeamLeader, new PublishAnnouncementDto
            {
                Title = "标题", Content = "内容", ScopeType = AnnouncementScopeType.Role, ScopeRoles = [UserRole.Admin]
            });
        }

        Assert.Equal(AnnouncementScopeType.DirectReports, ann.ScopeType);
        using var check = CreateContext();
        var audience = await check.AnnouncementReads.Where(r => r.AnnouncementId == ann.Id).Select(r => r.UserId).ToListAsync();
        Assert.Equal([reportId], audience);
    }
}
