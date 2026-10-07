using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 审批节点生成：一级/二级、无审批人名单、兜底管理员、选人校验。
/// </summary>
public class ApprovalStepRoutingTests : SqliteTestBase
{
    // ── 审批节点生成（原来这段逻辑删掉测试也全绿）──────────────────────────

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
}
