using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 覆盖 ApprovalService 的审批状态机并发安全（2026-09-17 代码审查发现并修复）：
/// 同一个审批节点被处理两次、审批和撤销几乎同时发生这两种场景，之前会导致状态不一致
/// （比如"整单显示已撤销，但考勤已经按通过回写"），修复用的是数据库层面的条件更新
/// （ExecuteUpdateAsync）+ 事务，这里用真实关系型数据库（SQLite 内存库——EF Core 的
/// InMemory provider 不支持 ExecuteUpdateAsync，测不出这条逻辑）验证修复后的行为。
///
/// 每个测试都用同一个内存 SQLite 连接、但各自 new 一个 AttendanceDbContext（模拟"两次独立的
/// HTTP 请求各自有自己的 DbContext，但操作同一张表"），这样才能真实复现"先查到 Pending、
/// 还没来得及写就被对方抢先处理掉"这类跨请求的竞态，而不是被同一个 DbContext 的一级缓存掩盖。
/// </summary>
public class ApprovalServiceConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private const int ApplicantId = 1;
    private const int ApproverId  = 2;
    private const int Approver2Id = 3;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());

    public ApprovalServiceConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = ApplicantId, EmployeeNo = "E001", RealName = "申请人", PasswordHash = "x" });
        db.Users.Add(new User { Id = ApproverId,  EmployeeNo = "E002", RealName = "审批人一", PasswordHash = "x" });
        db.Users.Add(new User { Id = Approver2Id, EmployeeNo = "E003", RealName = "审批人二", PasswordHash = "x" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AttendanceDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AttendanceDbContext(options);
    }

    /// <summary>建一张单级审批的申请单（只有一个审批节点），返回它的 Id。</summary>
    private int SeedSingleStepRequest()
    {
        using var db = CreateContext();
        var request = new ApprovalRequest
        {
            RequestNo = "TEST0001", ApplicantUserId = ApplicantId,
            ApprovalType = ApprovalType.Leave, ApprovalStatus = ApprovalStatus.Pending,
            LeaveStartTime = DateTime.Today, LeaveEndTime = DateTime.Today.AddHours(4)
        };
        db.ApprovalRequests.Add(request);
        db.SaveChanges();
        db.ApprovalSteps.Add(new ApprovalStep
        {
            ApprovalRequestId = request.Id, ApproverUserId = ApproverId,
            StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending
        });
        db.SaveChanges();
        return request.Id;
    }

    /// <summary>建一张两级审批的申请单，返回它的 Id。</summary>
    private int SeedTwoStepRequest()
    {
        using var db = CreateContext();
        var request = new ApprovalRequest
        {
            RequestNo = "TEST0002", ApplicantUserId = ApplicantId,
            ApprovalType = ApprovalType.Leave, ApprovalStatus = ApprovalStatus.Pending,
            LeaveStartTime = DateTime.Today, LeaveEndTime = DateTime.Today.AddHours(4)
        };
        db.ApprovalRequests.Add(request);
        db.SaveChanges();
        db.ApprovalSteps.AddRange(
            new ApprovalStep { ApprovalRequestId = request.Id, ApproverUserId = ApproverId,  StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending },
            new ApprovalStep { ApprovalRequestId = request.Id, ApproverUserId = Approver2Id, StepOrder = 2, ApprovalStatus = ApprovalStatus.Pending });
        db.SaveChanges();
        return request.Id;
    }

    [Fact]
    public async Task 单级审批通过后_整单变已通过_并且回写一次考勤()
    {
        var requestId = SeedSingleStepRequest();
        using var db = CreateContext();
        var fake = new FakeAttendanceService();
        var svc = new ApprovalService(db, fake, AppOptions);

        var ok = await svc.HandleApprovalAsync(ApproverId, new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });

        Assert.True(ok);
        Assert.Single(fake.UpdatedApprovalRequestIds);
        Assert.Equal(requestId, fake.UpdatedApprovalRequestIds[0]);

        using var verify = CreateContext();
        var request = await verify.ApprovalRequests.FindAsync(requestId);
        Assert.Equal(ApprovalStatus.Approved, request!.ApprovalStatus);
    }

    /// <summary>回归测试：同一个审批节点被处理两次（管理员手滑双击/网络重试），
    /// 第二次必须被拒绝，考勤不能被重复回写（原始 bug：加班时长会被累加两次）。</summary>
    [Fact]
    public async Task 同一个审批节点被处理两次_第二次直接失败_考勤不会被回写两次()
    {
        var requestId = SeedSingleStepRequest();
        var fake = new FakeAttendanceService();

        using (var db1 = CreateContext())
        {
            var svc1 = new ApprovalService(db1, fake, AppOptions);
            var first = await svc1.HandleApprovalAsync(ApproverId, new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });
            Assert.True(first);
        }

        using (var db2 = CreateContext())
        {
            var svc2 = new ApprovalService(db2, fake, AppOptions);
            var second = await svc2.HandleApprovalAsync(ApproverId, new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });
            Assert.False(second);
        }

        Assert.Single(fake.UpdatedApprovalRequestIds);   // 只回写了一次，没有被第二次调用重复触发
    }

    /// <summary>
    /// 回归测试：审批人正在处理这张单的时候，整单已经被撤销（模拟"申请人撤销"和"审批人通过"
    /// 两个请求几乎同时到达，撤销那边先在数据库里生效"）。HandleApprovalAsync 抢占节点那一步
    /// 会先成功（此时节点还是 Pending），但接下来"抢占整单状态"那一步会发现整单已经不是
    /// Pending/InProgress，识别出竞态、整个事务回滚——节点状态被打回 Pending，考勤完全不会被回写。
    /// 这正是本次修复要保证的核心行为：不会出现"整单显示已撤销，但考勤已经按通过处理"的不一致。
    /// </summary>
    [Fact]
    public async Task 处理审批过程中整单已被撤销_识别竞态并回滚_不会回写考勤()
    {
        var requestId = SeedSingleStepRequest();

        // 直接在数据库层面模拟"撤销先一步生效"：只改整单状态，不动节点状态——
        // 这正是 HandleApprovalAsync 抢占节点成功之后、抢占整单状态之前，可能撞见的中间状态。
        using (var db = CreateContext())
        {
            var request = await db.ApprovalRequests.FindAsync(requestId);
            request!.ApprovalStatus = ApprovalStatus.Cancelled;
            await db.SaveChangesAsync();
        }

        var fake = new FakeAttendanceService();
        using var handleDb = CreateContext();
        var svc = new ApprovalService(handleDb, fake, AppOptions);
        var ok = await svc.HandleApprovalAsync(ApproverId, new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });

        Assert.False(ok);
        Assert.Empty(fake.UpdatedApprovalRequestIds);   // 考勤完全没有被回写

        using var verify = CreateContext();
        var step = await verify.ApprovalSteps.FirstAsync(s => s.ApprovalRequestId == requestId);
        Assert.Equal(ApprovalStatus.Pending, step.ApprovalStatus);   // 节点的"抢占"被事务回滚，打回待审批
        var finalRequest = await verify.ApprovalRequests.FindAsync(requestId);
        Assert.Equal(ApprovalStatus.Cancelled, finalRequest!.ApprovalStatus);   // 整单仍然是撤销那次生效的状态
    }

    [Fact]
    public async Task 撤销待审批的申请_成功_并连带作废待处理节点()
    {
        var requestId = SeedSingleStepRequest();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);

        var ok = await svc.CancelApprovalAsync(ApplicantId, requestId);
        Assert.True(ok);

        using var verify = CreateContext();
        var request = await verify.ApprovalRequests.FindAsync(requestId);
        Assert.Equal(ApprovalStatus.Cancelled, request!.ApprovalStatus);
        var step = await verify.ApprovalSteps.FirstAsync(s => s.ApprovalRequestId == requestId);
        Assert.Equal(ApprovalStatus.Cancelled, step.ApprovalStatus);
    }

    [Fact]
    public async Task 已经审批通过的申请_不能再被撤销()
    {
        var requestId = SeedSingleStepRequest();
        using (var db = CreateContext())
        {
            var request = await db.ApprovalRequests.FindAsync(requestId);
            request!.ApprovalStatus = ApprovalStatus.Approved;
            await db.SaveChangesAsync();
        }

        using var cancelDb = CreateContext();
        var svc = new ApprovalService(cancelDb, new FakeAttendanceService(), AppOptions);
        var ok = await svc.CancelApprovalAsync(ApplicantId, requestId);

        Assert.False(ok);
    }

    [Fact]
    public async Task 两级审批_前一级还没审_后一级不能先审()
    {
        var requestId = SeedTwoStepRequest();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);

        // 第二级审批人想直接审，但第一级还是 Pending，不该被允许（禁止越级）
        var ok = await svc.HandleApprovalAsync(Approver2Id, new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });
        Assert.False(ok);
    }

    [Fact]
    public async Task 两级审批_通过第一级后变审批中_通过第二级后才最终通过并回写考勤()
    {
        var requestId = SeedTwoStepRequest();
        var fake = new FakeAttendanceService();

        using (var db1 = CreateContext())
        {
            var svc1 = new ApprovalService(db1, fake, AppOptions);
            var ok1 = await svc1.HandleApprovalAsync(ApproverId, new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });
            Assert.True(ok1);
        }

        using (var verify1 = CreateContext())
        {
            var request = await verify1.ApprovalRequests.FindAsync(requestId);
            Assert.Equal(ApprovalStatus.InProgress, request!.ApprovalStatus);   // 还没到最后一级，不是"已通过"
        }
        Assert.Empty(fake.UpdatedApprovalRequestIds);   // 第一级通过还不该回写考勤

        using (var db2 = CreateContext())
        {
            var svc2 = new ApprovalService(db2, fake, AppOptions);
            var ok2 = await svc2.HandleApprovalAsync(Approver2Id, new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });
            Assert.True(ok2);
        }

        using var verify2 = CreateContext();
        var final = await verify2.ApprovalRequests.FindAsync(requestId);
        Assert.Equal(ApprovalStatus.Approved, final!.ApprovalStatus);
        Assert.Single(fake.UpdatedApprovalRequestIds);   // 最后一级通过才回写一次考勤
    }

    [Fact]
    public async Task 两级审批_第一级驳回_第二级待处理节点跟着作废_不回写考勤()
    {
        var requestId = SeedTwoStepRequest();
        var fake = new FakeAttendanceService();
        using var db = CreateContext();
        var svc = new ApprovalService(db, fake, AppOptions);

        var ok = await svc.HandleApprovalAsync(ApproverId, new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = false, Comment = "不批" });
        Assert.True(ok);

        using var verify = CreateContext();
        var request = await verify.ApprovalRequests.FindAsync(requestId);
        Assert.Equal(ApprovalStatus.Rejected, request!.ApprovalStatus);
        var step2 = await verify.ApprovalSteps.FirstAsync(s => s.ApprovalRequestId == requestId && s.StepOrder == 2);
        Assert.Equal(ApprovalStatus.Cancelled, step2.ApprovalStatus);   // 后面还没轮到的节点被一并作废
        Assert.Empty(fake.UpdatedApprovalRequestIds);   // 驳回不回写考勤
    }
}
