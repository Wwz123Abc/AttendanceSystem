using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>每月最多补 3 次卡：按"已通过"算，补卡日期所在的月；文员、管理员不限；2026-11-01 起的补卡日期才套用。</summary>
public class PunchReplenishmentQuotaTests : SqliteTestBase
{
    private static readonly DateOnly Nov10 = new(2026, 11, 10);

    private int SeedUser(UserRole role)
    {
        using var db = CreateContext();
        var u = new User { EmployeeNo = "Q" + role, RealName = "人" + role, PasswordHash = "x", IsActive = true, Role = role };
        db.Users.Add(u);
        db.SaveChanges();
        return u.Id;
    }

    private void AddPunchRequest(int uid, DateOnly punchDate, ApprovalStatus status, string no)
    {
        using var db = CreateContext();
        db.ApprovalRequests.Add(new ApprovalRequest
        {
            RequestNo = no, ApplicantUserId = uid, ApprovalType = ApprovalType.PunchReplenishment, ApprovalStatus = status,
            PunchDate = punchDate, PunchType = PunchType.ClockOut, PunchTime = new TimeOnly(18, 0), Reason = "t"
        });
        db.SaveChanges();
    }

    private async Task<string?> QuotaAsync(int uid, DateOnly date, int? exclude = null)
    {
        using var db = CreateContext();
        return await PunchReplenishmentRules.FindQuotaProblemAsync(db, uid, date, exclude);
    }

    [Fact]
    public async Task 本月已通过3次_第4次被拦_提示联系文员()
    {
        var uid = SeedUser(UserRole.Employee);
        for (var i = 1; i <= 3; i++) AddPunchRequest(uid, new DateOnly(2026, 11, i), ApprovalStatus.Approved, "Q" + i);
        var msg = await QuotaAsync(uid, Nov10);
        Assert.NotNull(msg);
        Assert.Contains("上限", msg);
        Assert.Contains("文员", msg);
    }

    [Fact]
    public async Task 本月只通过2次_还能补()
    {
        var uid = SeedUser(UserRole.Employee);
        for (var i = 1; i <= 2; i++) AddPunchRequest(uid, new DateOnly(2026, 11, i), ApprovalStatus.Approved, "Q" + i);
        Assert.Null(await QuotaAsync(uid, Nov10));
    }

    [Fact]
    public async Task 待审批_已驳回_已撤销的不算次数_别的月和别人的也不算()
    {
        var uid = SeedUser(UserRole.Employee);
        AddPunchRequest(uid, new DateOnly(2026, 11, 1), ApprovalStatus.Approved, "Q1");
        AddPunchRequest(uid, new DateOnly(2026, 11, 2), ApprovalStatus.Pending, "Q2");
        AddPunchRequest(uid, new DateOnly(2026, 11, 3), ApprovalStatus.Rejected, "Q3");
        AddPunchRequest(uid, new DateOnly(2026, 11, 4), ApprovalStatus.Cancelled, "Q4");
        AddPunchRequest(uid, new DateOnly(2026, 12, 1), ApprovalStatus.Approved, "Q5");   // 12 月的不算 11 月
        AddPunchRequest(uid, new DateOnly(2026, 12, 2), ApprovalStatus.Approved, "Q6");
        AddPunchRequest(uid, new DateOnly(2026, 12, 3), ApprovalStatus.Approved, "Q7");
        Assert.Null(await QuotaAsync(uid, Nov10));
        Assert.NotNull(await QuotaAsync(uid, new DateOnly(2026, 12, 20)));   // 12 月自己已经 3 次
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Clerk)]
    public async Task 文员和管理员不限次数(UserRole role)
    {
        var uid = SeedUser(role);
        for (var i = 1; i <= 5; i++) AddPunchRequest(uid, new DateOnly(2026, 11, i), ApprovalStatus.Approved, "Q" + i);
        Assert.Null(await QuotaAsync(uid, Nov10));
    }

    [Fact]
    public async Task 十一月一日之前的补卡日期不套用限制()
    {
        var uid = SeedUser(UserRole.Employee);
        for (var i = 1; i <= 6; i++) AddPunchRequest(uid, new DateOnly(2026, 10, i), ApprovalStatus.Approved, "Q" + i);
        Assert.Null(await QuotaAsync(uid, new DateOnly(2026, 10, 20)));
    }

    [Fact]
    public async Task 审批回写时不把这张单自己算进去()
    {
        var uid = SeedUser(UserRole.Employee);
        for (var i = 1; i <= 3; i++) AddPunchRequest(uid, new DateOnly(2026, 11, i), ApprovalStatus.Approved, "Q" + i);
        int thirdId;
        using (var db = CreateContext()) thirdId = db.ApprovalRequests.Single(a => a.RequestNo == "Q3").Id;
        Assert.Null(await QuotaAsync(uid, new DateOnly(2026, 11, 3), thirdId));    // 它自己是第 3 次，别的只有 2 次
        Assert.NotNull(await QuotaAsync(uid, new DateOnly(2026, 11, 3), -1));      // 不排除任何一张就是 3 次
    }

    [Fact]
    public async Task 提交补卡申请_本月已通过3次_直接拒绝()
    {
        var uid = SeedUser(UserRole.Employee);
        for (var i = 1; i <= 3; i++) AddPunchRequest(uid, new DateOnly(2026, 11, i), ApprovalStatus.Approved, "Q" + i);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions, new FixedClock(new DateTime(2026, 11, 12, 10, 0, 0)));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.PunchReplenishment,
            PunchDate = Nov10, PunchType = PunchType.ClockOut, PunchTime = new TimeOnly(18, 0), Reason = "t"
        }));
        Assert.Contains("上限", ex.Message);
    }
}
