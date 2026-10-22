using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>申请有效期 15 天、超时自动作废，只对 2026-10-23 起提交的申请生效（2026-10-22 用户确认）。</summary>
public class ApprovalExpiryTests : SqliteTestBase
{
    private async Task<int> AddRequestAsync(string no, DateTime submittedAt, ApprovalStatus status = ApprovalStatus.Pending)
    {
        var (applicant, _, groupApprover, _) = SeedApprovalWorldOnce();
        using var db = CreateContext();
        var r = new ApprovalRequest
        {
            RequestNo = no, ApplicantUserId = applicant, ApprovalType = ApprovalType.Leave, ApprovalStatus = status,
            SubmittedAt = submittedAt, UpdatedAt = submittedAt, Reason = "t",
            LeaveStartTime = submittedAt, LeaveEndTime = submittedAt.AddHours(4), LeaveDurationHours = 4
        };
        db.ApprovalRequests.Add(r);
        await db.SaveChangesAsync();
        db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = r.Id, ApproverUserId = groupApprover, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending, CreatedAt = submittedAt });
        await db.SaveChangesAsync();
        return r.Id;
    }

    private (int applicant, int supervisor, int groupApprover, int admin)? _world;
    private (int applicant, int supervisor, int groupApprover, int admin) SeedApprovalWorldOnce() =>
        _world ??= SeedApprovalWorld(ApprovalLevelType.Level1, withGroupApprovers: true);

    private async Task<int> ExpireAtAsync(DateTime now)
    {
        using var db = CreateContext();
        return await new ApprovalService(db, new FakeAttendanceService(), AppOptions, new FixedClock(now)).ExpireStaleApprovalsAsync();
    }

    [Fact]
    public async Task 新申请超过15天没处理_自动作废_审批节点和通知同步处理()
    {
        var id = await AddRequestAsync("QJ-X-1", new DateTime(2026, 11, 1, 9, 0, 0));
        Assert.Equal(1, await ExpireAtAsync(new DateTime(2026, 11, 17, 9, 1, 0)));   // 提交满 15 天零 1 分钟
        using var db = CreateContext();
        Assert.Equal(ApprovalStatus.Expired, (await db.ApprovalRequests.SingleAsync(a => a.Id == id)).ApprovalStatus);
        Assert.All(db.ApprovalSteps.Where(s => s.ApprovalRequestId == id), s => Assert.Equal(ApprovalStatus.Cancelled, s.ApprovalStatus));
        Assert.Contains(db.Notifications, n => n.RelatedId == id && n.Title == "申请已作废");
    }

    [Fact]
    public async Task 没满15天的新申请_不作废()
    {
        var id = await AddRequestAsync("QJ-X-2", new DateTime(2026, 11, 1, 9, 0, 0));
        Assert.Equal(0, await ExpireAtAsync(new DateTime(2026, 11, 15, 9, 0, 0)));
        using var db = CreateContext();
        Assert.Equal(ApprovalStatus.Pending, (await db.ApprovalRequests.SingleAsync(a => a.Id == id)).ApprovalStatus);
    }

    [Fact]
    public async Task 老申请_10月23日之前提交的_就算超过15天也不作废()
    {
        var id = await AddRequestAsync("JB-X-3", new DateTime(2026, 10, 10, 9, 0, 0));   // 上线前提交的老申请
        Assert.Equal(0, await ExpireAtAsync(new DateTime(2026, 12, 1)));
        using var db = CreateContext();
        Assert.Equal(ApprovalStatus.Pending, (await db.ApprovalRequests.SingleAsync(a => a.Id == id)).ApprovalStatus);
    }

    [Fact]
    public async Task 已通过的新申请_不受影响_重复运行结果不变()
    {
        var done = await AddRequestAsync("QJ-X-4", new DateTime(2026, 11, 1, 9, 0, 0), ApprovalStatus.Approved);
        var open = await AddRequestAsync("QJ-X-5", new DateTime(2026, 11, 1, 9, 0, 0));
        Assert.Equal(1, await ExpireAtAsync(new DateTime(2026, 12, 1)));
        Assert.Equal(0, await ExpireAtAsync(new DateTime(2026, 12, 1)));   // 幂等
        using var db = CreateContext();
        Assert.Equal(ApprovalStatus.Approved, (await db.ApprovalRequests.SingleAsync(a => a.Id == done)).ApprovalStatus);
        Assert.Equal(ApprovalStatus.Expired, (await db.ApprovalRequests.SingleAsync(a => a.Id == open)).ApprovalStatus);
    }
}
