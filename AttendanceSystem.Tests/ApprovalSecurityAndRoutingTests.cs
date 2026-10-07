using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 审批安全与流转：附件地址校验、上级不能是本人、审批人停用/降级后自动改派、审批中可撤销。
/// </summary>
public class ApprovalSecurityAndRoutingTests : SqliteTestBase
{
    // ── 第 18 轮审查：附件地址 XSS / 上级=本人 / 审批人停用后的待办 ─────────────────────────────

    [Fact]
    public void 附件地址_只接受系统自己保存的格式_脚本_外链_别人的目录_怪后缀都拒绝()
    {
        var guid = "0123456789abcdef0123456789abcdef";
        void Check(string url) => ApprovalService.ValidateAttachmentUrls([url], 7, "uploads");
        Check($"/uploads/approvals/7/{guid}.jpg");     // 正常的不抛
        Check($"/uploads/approvals/7/{guid}.pdf");

        foreach (var bad in new[]
        {
            "javascript:alert(1)",
            "\" onmouseover=\"alert(1)",
            "<img src=x onerror=alert(1)>",
            "https://evil.example.com/a.jpg",
            $"/uploads/approvals/8/{guid}.jpg",          // 别人的目录
            $"/uploads/approvals/7/../8/{guid}.jpg",
            $"/uploads/approvals/7/{guid}.html",         // 不允许的后缀
            $"/uploads/approvals/7/{guid}.jpg\"><script>alert(1)</script>",
            "/uploads/approvals/7/short.jpg",
        })
            Assert.Throws<InvalidOperationException>(() => Check(bad));

        Assert.Throws<InvalidOperationException>(() =>
            ApprovalService.ValidateAttachmentUrls(Enumerable.Repeat($"/uploads/approvals/7/{guid}.jpg", 21).ToList(), 7, "uploads"));
        ApprovalService.ValidateAttachmentUrls([], 7, "uploads");   // 没有附件不抛
    }

    [Fact]
    public async Task 直属上级设成本人_保存员工时被拦住()
    {
        using var db = CreateContext();
        var (svc, adminId) = NewUserService(db);
        var emp = U("SELF1", "自己当上级的人");
        db.Users.Add(emp);
        await db.SaveChangesAsync();

        emp.SupervisorUserId = emp.Id;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateUserAsync(emp, adminId));
        Assert.Contains("不能是员工本人", ex.Message);
    }

    [Fact]
    public async Task 审批人解析_上级是本人或已停用时改用兜底管理员_上级正常就用上级()
    {
        using var db = CreateContext();
        var admin = U("ADM9", "兜底管理员"); admin.Role = UserRole.Admin;
        var boss  = U("BOSS", "正常上级"); boss.Role = UserRole.Supervisor;
        var gone  = U("GONE", "已停用的上级"); gone.IsActive = false;
        db.Users.AddRange(admin, boss, gone);
        await db.SaveChangesAsync();

        var selfRef = U("E1", "上级是自己"); db.Users.Add(selfRef); await db.SaveChangesAsync();
        selfRef.SupervisorUserId = selfRef.Id; await db.SaveChangesAsync();
        var withGone = U("E2", "上级已停用"); withGone.SupervisorUserId = gone.Id;
        var normal   = U("E3", "上级正常"); normal.SupervisorUserId = boss.Id;
        db.Users.AddRange(withGone, normal); await db.SaveChangesAsync();

        Assert.Equal(admin.Id, await ApproverResolver.ResolveSupervisorOrFallbackAsync(db, selfRef));   // 不能自己批自己
        Assert.Equal(admin.Id, await ApproverResolver.ResolveSupervisorOrFallbackAsync(db, withGone));  // 不派给已停用的人
        Assert.Equal(boss.Id,  await ApproverResolver.ResolveSupervisorOrFallbackAsync(db, normal));
    }

    [Fact]
    public async Task 停用审批人_他名下待处理的审批节点自动改派给申请人的上级并发通知()
    {
        int approverId, bossId, stepId, requestId;
        using (var db = CreateContext())
        {
            var boss      = U("B1", "申请人上级"); boss.Role = UserRole.Supervisor;
            var oldAppr   = U("B2", "即将离职的审批人");
            db.Users.AddRange(boss, oldAppr);
            await db.SaveChangesAsync();
            var applicant = U("B3", "申请人"); applicant.SupervisorUserId = boss.Id;
            db.Users.Add(applicant); await db.SaveChangesAsync();
            var req = new ApprovalRequest { RequestNo = "JB-9", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.InProgress };
            db.ApprovalRequests.Add(req); await db.SaveChangesAsync();
            var step = new ApprovalStep { ApprovalRequestId = req.Id, ApproverUserId = oldAppr.Id, StepOrder = 2, ApprovalStatus = ApprovalStatus.Pending };
            db.ApprovalSteps.Add(step); await db.SaveChangesAsync();
            approverId = oldAppr.Id; bossId = boss.Id; stepId = step.Id; requestId = req.Id;
        }

        using (var db = CreateContext())
        {
            var (svc, adminId) = NewUserService(db);
            Assert.True(await svc.DeactivateUserAsync(approverId, adminId));
        }

        using var check = CreateContext();
        Assert.Equal(bossId, (await check.ApprovalSteps.FindAsync(stepId))!.ApproverUserId);
        Assert.True(await check.Notifications.AnyAsync(n => n.UserId == bossId && n.RelatedId == requestId));
    }

    [Fact]
    public async Task 申请人可以撤销审批中的单_待处理节点一并作废()
    {
        int userId, reqId;
        using (var db = CreateContext())
        {
            var applicant = U("C1", "申请人"); var approver = U("C2", "二级审批人");
            db.Users.AddRange(applicant, approver); await db.SaveChangesAsync();
            var req = new ApprovalRequest { RequestNo = "JB-8", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.InProgress };
            db.ApprovalRequests.Add(req); await db.SaveChangesAsync();
            db.ApprovalSteps.AddRange(
                new ApprovalStep { ApprovalRequestId = req.Id, ApproverUserId = approver.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Approved },
                new ApprovalStep { ApprovalRequestId = req.Id, ApproverUserId = approver.Id, StepOrder = 2, ApprovalStatus = ApprovalStatus.Pending });
            await db.SaveChangesAsync();
            userId = applicant.Id; reqId = req.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
            Assert.True(await svc.CancelApprovalAsync(userId, reqId));
        }
        using var check = CreateContext();
        Assert.Equal(ApprovalStatus.Cancelled, (await check.ApprovalRequests.FindAsync(reqId))!.ApprovalStatus);
        Assert.DoesNotContain(await check.ApprovalSteps.Where(x => x.ApprovalRequestId == reqId).ToListAsync(), x => x.ApprovalStatus == ApprovalStatus.Pending);
    }

    // ── 第 18 轮审查（续）：上级/名单审批人被降成普通员工后不能再收到审批单 ────────────────────

    [Fact]
    public async Task 审批人解析_直属上级被降成普通员工_改用兜底管理员()
    {
        using var db = CreateContext();
        var admin = U("ADM8", "兜底管理员"); admin.Role = UserRole.Admin;
        var demoted = U("DEM1", "已降成普通员工的原主管"); demoted.Role = UserRole.Employee;
        db.Users.AddRange(admin, demoted);
        await db.SaveChangesAsync();
        var emp = U("E9", "他的下属"); emp.SupervisorUserId = demoted.Id;
        db.Users.Add(emp); await db.SaveChangesAsync();

        Assert.Equal(admin.Id, await ApproverResolver.ResolveSupervisorOrFallbackAsync(db, emp));
        demoted.Role = UserRole.Supervisor; await db.SaveChangesAsync();   // 还是主管就照常
        Assert.Equal(demoted.Id, await ApproverResolver.ResolveSupervisorOrFallbackAsync(db, emp));
    }

    [Fact]
    public async Task 可选审批人名单_不含已降成普通员工的人()
    {
        using var db = CreateContext();
        var leader = U("TL1", "还是班组长"); leader.Role = UserRole.TeamLeader;
        var demoted = U("TL2", "已降级的班组长"); demoted.Role = UserRole.Employee;
        var group = new AttendanceGroup { GroupName = "G-名单", CompanyName = "公司" };
        db.AddRange(leader, demoted, group);
        await db.SaveChangesAsync();
        var emp = U("EMP-G", "组员"); emp.AttendanceGroupId = group.Id;
        db.Users.Add(emp);
        db.AttendanceGroupApprovers.AddRange(
            new AttendanceGroupApprover { AttendanceGroupId = group.Id, UserId = leader.Id },
            new AttendanceGroupApprover { AttendanceGroupId = group.Id, UserId = demoted.Id });
        await db.SaveChangesAsync();

        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var options = await svc.GetAvailableApproversAsync(emp.Id);
        Assert.Single(options);
        Assert.Equal(leader.Id, options[0].UserId);
    }

    [Fact]
    public async Task 角色从主管降成普通员工_他名下待处理的审批节点自动改派()
    {
        int supId, bossId, stepId;
        using (var db = CreateContext())
        {
            var boss = U("BS1", "申请人上级"); boss.Role = UserRole.Supervisor;
            var sup  = U("BS2", "要被降级的主管"); sup.Role = UserRole.Supervisor;
            db.Users.AddRange(boss, sup); await db.SaveChangesAsync();
            var applicant = U("BS3", "申请人"); applicant.SupervisorUserId = boss.Id;
            db.Users.Add(applicant); await db.SaveChangesAsync();
            var req = new ApprovalRequest { RequestNo = "JB-7", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Pending };
            db.ApprovalRequests.Add(req); await db.SaveChangesAsync();
            var step = new ApprovalStep { ApprovalRequestId = req.Id, ApproverUserId = sup.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending };
            db.ApprovalSteps.Add(step); await db.SaveChangesAsync();
            supId = sup.Id; bossId = boss.Id; stepId = step.Id;
        }
        using (var db = CreateContext())
        {
            var (svc, adminId) = NewUserService(db);
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == supId);
            u.Role = UserRole.Employee;
            Assert.True(await svc.UpdateUserAsync(u, adminId));
        }
        using var check = CreateContext();
        Assert.Equal(bossId, (await check.ApprovalSteps.FindAsync(stepId))!.ApproverUserId);
    }

    [Fact]
    public async Task 改派_申请人的直属上级已经是同一张单的另一级审批人_换成兜底管理员_不让同一个人点两次()
    {
        int oldId, adminId, stepId;
        using (var db = CreateContext())
        {
            var admin = U("ADMX", "兜底管理员"); admin.Role = UserRole.Admin;
            var boss  = U("BX1", "直属上级（本来就是第二级）"); boss.Role = UserRole.Supervisor;
            var old1  = U("BX2", "第一级审批人（将离职）"); old1.Role = UserRole.TeamLeader;
            db.Users.AddRange(admin, boss, old1); await db.SaveChangesAsync();
            var applicant = U("BX3", "申请人"); applicant.SupervisorUserId = boss.Id;
            db.Users.Add(applicant); await db.SaveChangesAsync();
            var req = new ApprovalRequest { RequestNo = "JB-6", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Pending };
            db.ApprovalRequests.Add(req); await db.SaveChangesAsync();
            var s1 = new ApprovalStep { ApprovalRequestId = req.Id, ApproverUserId = old1.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending };
            var s2 = new ApprovalStep { ApprovalRequestId = req.Id, ApproverUserId = boss.Id, StepOrder = 2, ApprovalStatus = ApprovalStatus.Pending };
            db.ApprovalSteps.AddRange(s1, s2); await db.SaveChangesAsync();
            oldId = old1.Id; adminId = admin.Id; stepId = s1.Id;
        }
        using (var db = CreateContext())
        {
            var (svc, actingAdminId) = NewUserService(db);
            Assert.True(await svc.DeactivateUserAsync(oldId, actingAdminId));
        }
        using var check = CreateContext();
        Assert.Equal(adminId, (await check.ApprovalSteps.FindAsync(stepId))!.ApproverUserId);
    }
}
