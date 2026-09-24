using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-09-24 全项目审查（第 10 轮）修复项的回归测试：
/// 角色层级（文员/分公司管理员能不能动总部管理员）、审批节点生成（一级/二级/无审批人/名单校验）、
/// 请假出差时长上限、手动重算月度汇总时空名单不能落到"全公司重算"。
/// </summary>
public class ReviewFixTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());

    public ReviewFixTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    private static CurrentUser Cu(UserRole role, int? scopedDept) => new() { UserId = 1, Role = role, ScopedDepartmentId = scopedDept };

    // ── 角色层级：CurrentUser.CanManageAccount ─────────────────────────────

    [Theory]
    // 操作者角色, 操作者范围, 目标角色, 目标范围, 期望
    [InlineData(UserRole.Admin, null, UserRole.Admin,    null, true)]    // 总部超管：谁都能操作
    [InlineData(UserRole.Admin, null, UserRole.Admin,    5,    true)]
    [InlineData(UserRole.Admin, 5,    UserRole.Admin,    null, false)]   // 分公司管理员不能动总部管理员（重置密码接管总部账号）
    [InlineData(UserRole.Admin, 5,    UserRole.Admin,    5,    true)]    // 分公司管理员之间维持可操作
    [InlineData(UserRole.Admin, 5,    UserRole.Clerk,    5,    true)]
    [InlineData(UserRole.Clerk, 5,    UserRole.Admin,    5,    false)]   // 文员不能动任何管理员
    [InlineData(UserRole.Clerk, 5,    UserRole.Admin,    null, false)]
    [InlineData(UserRole.Clerk, null, UserRole.Admin,    null, false)]   // 不带范围的文员同样不行
    [InlineData(UserRole.Clerk, 5,    UserRole.Employee, 5,    true)]    // 文员管普通员工照常
    [InlineData(UserRole.Clerk, 5,    UserRole.Supervisor, null, true)]
    public void 角色层级判断(UserRole opRole, int? opScope, UserRole targetRole, int? targetScope, bool expected)
        => Assert.Equal(expected, Cu(opRole, opScope).CanManageAccount(targetRole, targetScope));

    [Fact]
    public void 总部超管判定()
    {
        Assert.True(Cu(UserRole.Admin, null).IsHqSuperAdmin);
        Assert.False(Cu(UserRole.Admin, 3).IsHqSuperAdmin);
        Assert.False(Cu(UserRole.Clerk, null).IsHqSuperAdmin);   // 不带范围的文员不是超管
    }

    // ── 审批节点生成（原来这段逻辑删掉测试也全绿）──────────────────────────

    private (int applicant, int supervisor, int groupApprover, int fallbackAdmin) SeedApprovalWorld(ApprovalLevelType level, bool withGroupApprovers)
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "测试组", ApprovalLevel = level };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var supervisor = new User { EmployeeNo = "S01", RealName = "直属上级", PasswordHash = "x", Role = UserRole.Supervisor, AttendanceGroupId = group.Id };
        var groupApprover = new User { EmployeeNo = "G01", RealName = "组审批人", PasswordHash = "x", Role = UserRole.TeamLeader, AttendanceGroupId = group.Id };
        var admin = new User { EmployeeNo = "A01", RealName = "兜底管理员", PasswordHash = "x", Role = UserRole.Admin };   // 不带范围 = 总部
        db.Users.AddRange(supervisor, groupApprover, admin);
        db.SaveChanges();

        var applicant = new User { EmployeeNo = "E01", RealName = "申请人", PasswordHash = "x", AttendanceGroupId = group.Id, SupervisorUserId = supervisor.Id };
        db.Users.Add(applicant);
        db.SaveChanges();

        if (withGroupApprovers)
        {
            db.AttendanceGroupApprovers.Add(new AttendanceGroupApprover { AttendanceGroupId = group.Id, UserId = groupApprover.Id });
            db.SaveChanges();
        }
        return (applicant.Id, supervisor.Id, groupApprover.Id, admin.Id);
    }

    private static SubmitApprovalDto PunchDto(int? approverId = null) => new()
    {
        ApprovalType = ApprovalType.PunchReplenishment,
        PunchDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
        PunchType = PunchType.ClockIn,
        PunchTime = new TimeOnly(9, 0),
        Reason = "忘打卡",
        ApproverUserId = approverId
    };

    [Fact]
    public async Task 二级审批_组里配了审批人名单_生成两个节点_先组审批人_再直属上级()
    {
        var w = SeedApprovalWorld(ApprovalLevelType.Level2, withGroupApprovers: true);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);

        var req = await svc.SubmitApprovalAsync(w.applicant, PunchDto(w.groupApprover));

        using var check = CreateContext();
        var steps = await check.ApprovalSteps.Where(s => s.ApprovalRequestId == req.Id).OrderBy(s => s.StepOrder).ToListAsync();
        Assert.Equal(2, steps.Count);
        Assert.Equal((1, w.groupApprover), (steps[0].StepOrder, steps[0].ApproverUserId));
        Assert.Equal((2, w.supervisor), (steps[1].StepOrder, steps[1].ApproverUserId));
    }

    [Fact]
    public async Task 二级审批_没配名单时一级就是直属上级_不再生成同一个人的第二个节点()
    {
        var w = SeedApprovalWorld(ApprovalLevelType.Level2, withGroupApprovers: false);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);

        var req = await svc.SubmitApprovalAsync(w.applicant, PunchDto());

        using var check = CreateContext();
        var steps = await check.ApprovalSteps.Where(s => s.ApprovalRequestId == req.Id).ToListAsync();
        var only = Assert.Single(steps);
        Assert.Equal(w.supervisor, only.ApproverUserId);
    }

    [Fact]
    public async Task 一级审批_只生成一个节点()
    {
        var w = SeedApprovalWorld(ApprovalLevelType.Level1, withGroupApprovers: true);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);

        var req = await svc.SubmitApprovalAsync(w.applicant, PunchDto(w.groupApprover));

        using var check = CreateContext();
        Assert.Single(await check.ApprovalSteps.Where(s => s.ApprovalRequestId == req.Id).ToListAsync());
    }

    [Fact]
    public async Task 组里配了名单_选的人不在名单里或不选_拒绝并且不留脏单()
    {
        var w = SeedApprovalWorld(ApprovalLevelType.Level1, withGroupApprovers: true);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(w.applicant, PunchDto(w.supervisor)));   // 直属上级不在名单里
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(w.applicant, PunchDto(null)));            // 不选

        using var check = CreateContext();
        Assert.Empty(await check.ApprovalRequests.ToListAsync());   // 申请单被连带删掉，不留审不掉的脏单
    }

    [Fact]
    public async Task 没有直属上级也没配名单_兜底找总部管理员()
    {
        var w = SeedApprovalWorld(ApprovalLevelType.Level1, withGroupApprovers: false);
        using (var db0 = CreateContext())
        {
            var applicant = await db0.Users.FindAsync(w.applicant);
            applicant!.SupervisorUserId = null;
            await db0.SaveChangesAsync();
        }
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);

        var req = await svc.SubmitApprovalAsync(w.applicant, PunchDto());

        using var check = CreateContext();
        var step = await check.ApprovalSteps.SingleAsync(s => s.ApprovalRequestId == req.Id);
        Assert.Equal(w.fallbackAdmin, step.ApproverUserId);
    }

    // ── 请假/出差时长上限 ──────────────────────────────────────────────────

    [Fact]
    public async Task 请假跨度超过上限_拒绝()
    {
        var w = SeedApprovalWorld(ApprovalLevelType.Level1, withGroupApprovers: false);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Now.AddHours(1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(w.applicant, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.AnnualLeave,
            LeaveStartTime = start, LeaveEndTime = new DateTime(9999, 12, 31),   // 提交"结束时间=9999 年"的假单
            Reason = "x"
        }));
        Assert.Contains("跨度", ex.Message);
    }

    [Fact]
    public async Task 出差跨度超过上限_拒绝()
    {
        var w = SeedApprovalWorld(ApprovalLevelType.Level1, withGroupApprovers: false);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Now.AddHours(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(w.applicant, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.BusinessTrip, BusinessTripStartTime = start,
            BusinessTripEndTime = start.AddDays(ApprovalService.MaxLeaveOrTripSpanDays + 1), Reason = "x"
        }));
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
