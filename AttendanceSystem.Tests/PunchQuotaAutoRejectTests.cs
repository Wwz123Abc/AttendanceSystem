using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 补卡超过每月 3 次上限时，审批通过必须“直接驳回”，不能留下“显示已通过、考勤没改”的半成品
/// （2026-11-16 用户要求；党佳豪 11-06、付争志 11-01 两张单就是排队期间名额被别人用掉、审批时回写抛异常卡住的）。
/// </summary>
public class PunchQuotaAutoRejectTests : SqliteTestBase
{
    private static readonly DateOnly Month = new(2026, 11, 1);

    private (int applicantId, int approverId) SeedApplicantAndApprover()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "补卡超限测试组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var shift = DayShift();
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        db.SaveChanges();

        var applicant = U("BKQ1", "补卡超限测试");
        applicant.AttendanceGroupId = group.Id;
        db.Users.Add(applicant);

        var approver = new User { EmployeeNo = "BKQA1", RealName = "总部审批员", PasswordHash = "x", Role = UserRole.Admin };
        db.Users.Add(approver);
        db.SaveChanges();
        return (applicant.Id, approver.Id);
    }

    private async Task SeedApprovedQuotaAsync(int applicantId)
    {
        using var db = CreateContext();
        for (var i = 0; i < PunchReplenishmentRules.MaxPerMonth; i++)
        {
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = $"BK-QUOTA-{i}-{Guid.NewGuid():N}"[..20], ApplicantUserId = applicantId,
                ApprovalType = ApprovalType.PunchReplenishment, ApprovalStatus = ApprovalStatus.Approved,
                PunchDate = Month.AddDays(i), PunchType = PunchType.ClockIn, PunchTime = new TimeOnly(8, 30),
                Reason = "已通过的补卡"
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedPendingRequestAsync(int applicantId, int approverId, DateOnly punchDate)
    {
        using var db = CreateContext();
        var request = new ApprovalRequest
        {
            RequestNo = "BK-QUOTA-PENDING-" + Guid.NewGuid().ToString("N")[..8], ApplicantUserId = applicantId,
            ApprovalType = ApprovalType.PunchReplenishment, ApprovalStatus = ApprovalStatus.Pending,
            PunchDate = punchDate, PunchType = PunchType.ClockIn, PunchTime = new TimeOnly(8, 30), Reason = "超限补卡"
        };
        db.ApprovalRequests.Add(request);
        await db.SaveChangesAsync();
        db.ApprovalSteps.Add(new ApprovalStep
        {
            ApprovalRequestId = request.Id, ApproverUserId = approverId,
            StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending
        });
        await db.SaveChangesAsync();
        return request.Id;
    }

    [Fact]
    public async Task 补卡超过每月上限_审批通过时直接驳回_不回写考勤()
    {
        var (applicantId, approverId) = SeedApplicantAndApprover();
        await SeedApprovedQuotaAsync(applicantId);
        var requestId = await SeedPendingRequestAsync(applicantId, approverId, Month.AddDays(8));

        using var db = CreateContext();
        var fake = new FakeAttendanceService();
        var svc = new ApprovalService(db, fake, AppOptions);

        var ok = await svc.HandleApprovalAsync(approverId,
            new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });

        Assert.True(ok);                                    // 处理成功（不是抛异常/500）
        Assert.Empty(fake.UpdatedApprovalRequestIds);       // 没有回写考勤

        using var verify = CreateContext();
        var request = await verify.ApprovalRequests.FindAsync(requestId);
        Assert.Equal(ApprovalStatus.Rejected, request!.ApprovalStatus);

        var step = await verify.ApprovalSteps.SingleAsync(s => s.ApprovalRequestId == requestId);
        Assert.Equal(ApprovalStatus.Rejected, step.ApprovalStatus);
        Assert.Contains("系统自动驳回", step.Comment ?? "");
        Assert.Contains("3 次", step.Comment ?? "");

        var notify = await verify.Notifications.FirstOrDefaultAsync(n => n.UserId == applicantId);
        Assert.NotNull(notify);
    }

    [Fact]
    public async Task 名额还没用完时_审批通过正常走回写()
    {
        var (applicantId, approverId) = SeedApplicantAndApprover();
        var requestId = await SeedPendingRequestAsync(applicantId, approverId, Month.AddDays(8));

        using var db = CreateContext();
        var fake = new FakeAttendanceService();
        var svc = new ApprovalService(db, fake, AppOptions);

        var ok = await svc.HandleApprovalAsync(approverId,
            new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });

        Assert.True(ok);
        Assert.Single(fake.UpdatedApprovalRequestIds);      // 正常回写一次
        Assert.Equal(requestId, fake.UpdatedApprovalRequestIds[0]);
    }

    /// <summary>
    /// 复审 P1 回归：两级审批的最后一节点触发自动驳回时，通知不能还按"已通过一级审批，待下一级审批"发。
    /// 原因是状态是 ExecuteUpdateAsync 直接改库的，内存里跟踪的申请单还是旧状态（InProgress），
    /// 不清跟踪器的话重新查出来仍是旧对象、通知就会写错。
    /// </summary>
    [Fact]
    public async Task 两级审批最后节点被自动驳回_通知内容是驳回而不是待下一级()
    {
        var (applicantId, approverId) = SeedApplicantAndApprover();
        await SeedApprovedQuotaAsync(applicantId);

        int requestId;
        using (var db = CreateContext())
        {
            var request = new ApprovalRequest
            {
                RequestNo = "BK-QUOTA-2STEP-" + Guid.NewGuid().ToString("N")[..8], ApplicantUserId = applicantId,
                ApprovalType = ApprovalType.PunchReplenishment, ApprovalStatus = ApprovalStatus.InProgress,
                PunchDate = Month.AddDays(8), PunchType = PunchType.ClockIn, PunchTime = new TimeOnly(8, 30), Reason = "超限补卡"
            };
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();
            db.ApprovalSteps.AddRange(
                new ApprovalStep
                {
                    ApprovalRequestId = request.Id, ApproverUserId = approverId, StepOrder = 1,
                    ApprovalStatus = ApprovalStatus.Approved, HandledAt = DateTime.Now.AddHours(-1)
                },
                new ApprovalStep
                {
                    ApprovalRequestId = request.Id, ApproverUserId = approverId, StepOrder = 2,
                    ApprovalStatus = ApprovalStatus.Pending
                });
            await db.SaveChangesAsync();
            requestId = request.Id;
        }

        using var db2 = CreateContext();
        var fake = new FakeAttendanceService();
        var svc = new ApprovalService(db2, fake, AppOptions);

        var ok = await svc.HandleApprovalAsync(approverId,
            new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true });

        Assert.True(ok);
        Assert.Empty(fake.UpdatedApprovalRequestIds);

        // 同一个 DbContext 里再查（审批页就是这么查的）：必须是新的"已驳回"，不能还是内存里的旧对象
        var sameContext = await db2.ApprovalRequests.FindAsync(requestId);
        Assert.Equal(ApprovalStatus.Rejected, sameContext!.ApprovalStatus);

        using var verify = CreateContext();
        var request2 = await verify.ApprovalRequests.FindAsync(requestId);
        Assert.Equal(ApprovalStatus.Rejected, request2!.ApprovalStatus);

        var step2 = await verify.ApprovalSteps.SingleAsync(s => s.ApprovalRequestId == requestId && s.StepOrder == 2);
        Assert.Equal(ApprovalStatus.Rejected, step2.ApprovalStatus);

        var notify = await verify.Notifications
            .Where(n => n.UserId == applicantId)
            .OrderByDescending(n => n.Id)
            .FirstAsync();
        Assert.Contains("已驳回", notify.Content);
        Assert.DoesNotContain("待下一级审批", notify.Content);
    }
}
