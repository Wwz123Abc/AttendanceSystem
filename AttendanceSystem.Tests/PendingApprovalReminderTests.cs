using System.Reflection;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
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

/// <summary>
/// 待审批提醒：按人汇总、两级审批、停用/调岗不提醒、旧提醒标已读、7 天清理。
/// </summary>
public class PendingApprovalReminderTests : SqliteTestBase
{
    // ── 待审批提醒（每 4 小时一次，2026-09-30 新增）────────────────────────

    private async Task RunRemindPendingApprovalsAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AttendanceDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IDeptScopeService, DeptScopeService>();
        using var provider = services.BuildServiceProvider();
        var svc = new AttendanceBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AttendanceBackgroundService>.Instance);
        var method = typeof(AttendanceBackgroundService).GetMethod("RemindPendingApprovalsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [])!;
    }

    [Fact]
    public async Task 待审批提醒_还没处理的申请_给当前该处理的人发提醒()
    {
        int approverId, requestId;
        using (var db = CreateContext())
        {
            var applicant = U("A6", "申请人甲");
            var approver  = U("S1", "审批人甲");
            db.Users.AddRange(applicant, approver);
            db.SaveChanges();
            approverId = approver.Id;

            var request = new ApprovalRequest
            {
                RequestNo = "QJ-RM-1", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Leave,
                ApprovalStatus = ApprovalStatus.Pending, LeaveStartTime = DateTime.Today, LeaveEndTime = DateTime.Today.AddHours(4),
                UpdatedAt = DateTime.Now.AddHours(-5)   // 挂了 5 小时，超过 4 小时的提醒门槛
            };
            db.ApprovalRequests.Add(request);
            db.SaveChanges();
            requestId = request.Id;
            db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = requestId, ApproverUserId = approverId, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        var notif = await check.Notifications.SingleAsync(n => n.UserId == approverId && n.NotificationType == "ApprovalPending");
        Assert.Null(notif.RelatedId);   // 汇总通知不对应某一张单
        Assert.Contains("1 张", notif.Content);
        Assert.Contains("没处理", notif.Content);
    }

    [Fact]
    public async Task 待审批提醒_两级审批_只提醒当前轮到的一级_不提醒还没轮到的二级()
    {
        int approver1Id, approver2Id;
        using (var db = CreateContext())
        {
            var applicant = U("A7", "申请人乙");
            var approver1 = U("S2", "一级审批人");
            var approver2 = U("S3", "二级审批人");
            db.Users.AddRange(applicant, approver1, approver2);
            db.SaveChanges();
            approver1Id = approver1.Id; approver2Id = approver2.Id;

            var request = new ApprovalRequest
            {
                RequestNo = "QJ-RM-2", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Leave,
                ApprovalStatus = ApprovalStatus.Pending, LeaveStartTime = DateTime.Today, LeaveEndTime = DateTime.Today.AddHours(4),
                UpdatedAt = DateTime.Now.AddHours(-5)
            };
            db.ApprovalRequests.Add(request);
            db.SaveChanges();
            db.ApprovalSteps.AddRange(
                new ApprovalStep { ApprovalRequestId = request.Id, ApproverUserId = approver1Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending },
                new ApprovalStep { ApprovalRequestId = request.Id, ApproverUserId = approver2Id, StepOrder = 2, ApprovalStatus = ApprovalStatus.Pending });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.NotificationType == "ApprovalPending"));
        Assert.True(await check.Notifications.AnyAsync(n => n.UserId == approver1Id));
        Assert.False(await check.Notifications.AnyAsync(n => n.UserId == approver2Id));
    }

    [Fact]
    public async Task 待审批提醒_已经审批通过的申请_不再提醒()
    {
        using (var db = CreateContext())
        {
            var applicant = U("A8", "申请人丙");
            var approver  = U("S4", "审批人丙");
            db.Users.AddRange(applicant, approver);
            db.SaveChanges();
            var request = new ApprovalRequest
            {
                RequestNo = "QJ-RM-3", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Leave,
                ApprovalStatus = ApprovalStatus.Approved, LeaveStartTime = DateTime.Today, LeaveEndTime = DateTime.Today.AddHours(4)
            };
            db.ApprovalRequests.Add(request);
            db.SaveChanges();
            db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = request.Id, ApproverUserId = approver.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Approved, HandledAt = DateTime.Now });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        Assert.Equal(0, await check.Notifications.CountAsync());
    }

    // 2026-09-30 第三方复核发现的 3 个问题：①刚提交（不到 4 小时）的单不应该提醒；②审批人已停用/已管不到
    // 申请人时不应该提醒；③同一张单已有未读提醒时不应该重复新增。下面 4 条测试逐一验证。

    [Fact]
    public async Task 待审批提醒_刚提交不到4小时的申请_这一轮不发提醒()
    {
        using (var db = CreateContext())
        {
            var applicant = U("A9", "申请人丁");
            var approver  = U("S5", "审批人丁");
            db.Users.AddRange(applicant, approver);
            db.SaveChanges();
            var request = new ApprovalRequest
            {
                RequestNo = "QJ-RM-4", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Leave,
                ApprovalStatus = ApprovalStatus.Pending, LeaveStartTime = DateTime.Today, LeaveEndTime = DateTime.Today.AddHours(4),
                UpdatedAt = DateTime.Now.AddHours(-1)   // 1 小时前刚提交/刚流转到这一级，还没到 4 小时门槛
            };
            db.ApprovalRequests.Add(request);
            db.SaveChanges();
            db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = request.Id, ApproverUserId = approver.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        Assert.Equal(0, await check.Notifications.CountAsync());
    }

    [Fact]
    public async Task 待审批提醒_审批人已停用_不发提醒()
    {
        using (var db = CreateContext())
        {
            var applicant = U("A10", "申请人戊");
            var approver  = U("S6", "已停用审批人");
            approver.IsActive = false;
            db.Users.AddRange(applicant, approver);
            db.SaveChanges();
            var request = new ApprovalRequest
            {
                RequestNo = "QJ-RM-5", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Leave,
                ApprovalStatus = ApprovalStatus.Pending, LeaveStartTime = DateTime.Today, LeaveEndTime = DateTime.Today.AddHours(4),
                UpdatedAt = DateTime.Now.AddHours(-5)
            };
            db.ApprovalRequests.Add(request);
            db.SaveChanges();
            db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = request.Id, ApproverUserId = approver.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        Assert.Equal(0, await check.Notifications.CountAsync());
    }

    [Fact]
    public async Task 待审批提醒_申请人调到审批人管不到的部门_不发提醒()
    {
        using (var db = CreateContext())
        {
            var deptA = new Department { DeptName = "分公司A", IsActive = true };
            var deptB = new Department { DeptName = "分公司B", IsActive = true };
            db.Departments.AddRange(deptA, deptB);
            db.SaveChanges();

            var applicant = U("A11", "申请人己");
            applicant.DepartmentId = deptB.Id;   // 申请提交后，申请人已经调去了分公司B
            var approver = U("S7", "分公司A审批人");
            approver.ScopedDepartmentId = deptA.Id;   // 审批人管理范围只在分公司A，管不到分公司B
            db.Users.AddRange(applicant, approver);
            db.SaveChanges();

            var request = new ApprovalRequest
            {
                RequestNo = "QJ-RM-6", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Leave,
                ApprovalStatus = ApprovalStatus.Pending, LeaveStartTime = DateTime.Today, LeaveEndTime = DateTime.Today.AddHours(4),
                UpdatedAt = DateTime.Now.AddHours(-5)
            };
            db.ApprovalRequests.Add(request);
            db.SaveChanges();
            db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = request.Id, ApproverUserId = approver.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        Assert.Equal(0, await check.Notifications.CountAsync());
    }

    // 2026-09-30 第三方复核第二轮发现：上一版"已有未读就跳过不发"的去重规则，会被提交/流转时发的那条
    // "您有新的待审批申请"（同样是 ApprovalPending 类型）挡住——审批人只要没点开最早那条通知，后面所有
    // 提醒都会被判定成"已经有未读"而永远不发，恰好是这个功能本来要解决的场景。改成"发新提醒前先把旧的
    // 未读标成已读"，下面重写这条测试验证新行为，并补一条走真实提交流程的测试（原来的测试是直接往库里
    // 插申请单，绕开了真实提交会发的那条初始通知，没能测出这个问题）。
    [Fact]
    public async Task 待审批提醒_上一轮提醒还没读_旧的标成已读_插入新的一条()
    {
        int approverId, requestId;
        using (var db = CreateContext())
        {
            var applicant = U("A12", "申请人庚");
            var approver  = U("S8", "审批人庚");
            db.Users.AddRange(applicant, approver);
            db.SaveChanges();
            approverId = approver.Id;

            var request = new ApprovalRequest
            {
                RequestNo = "QJ-RM-7", ApplicantUserId = applicant.Id, ApprovalType = ApprovalType.Leave,
                ApprovalStatus = ApprovalStatus.Pending, LeaveStartTime = DateTime.Today, LeaveEndTime = DateTime.Today.AddHours(4),
                UpdatedAt = DateTime.Now.AddHours(-5)
            };
            db.ApprovalRequests.Add(request);
            db.SaveChanges();
            requestId = request.Id;
            db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = requestId, ApproverUserId = approverId, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending });
            // 模拟"上一轮已经提醒过、审批人还没读"
            db.Notifications.Add(new Notification
            {
                UserId = approverId, Title = "审批提醒", Content = "上一轮的提醒",
                NotificationType = "ApprovalPending", RelatedId = requestId, IsRead = false, CreatedAt = DateTime.Now.AddHours(-4.5)
            });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        var all = await check.Notifications.Where(n => n.UserId == approverId).ToListAsync();
        Assert.Equal(2, all.Count);                                    // 旧的还在，新增了一条
        Assert.Single(all, n => !n.IsRead);                            // 未读的只剩新的这一条
        Assert.Single(all, n => n.IsRead && n.Content == "上一轮的提醒");   // 旧的已经被标成已读，内容没变
    }

    [Fact]
    public async Task 待审批提醒_走真实提交流程_没点开提交时的通知_挂5小时后仍能收到提醒()
    {
        int approverId, applicantId;
        using (var db = CreateContext())
        {
            var applicant = U("A13", "申请人辛");
            var approver  = U("S9", "审批人辛");
            approver.Role = UserRole.TeamLeader;   // 名单里的审批人必须是审批类角色（降成普通员工的人不再算）
            db.Users.AddRange(applicant, approver);
            db.SaveChanges();
            applicantId = applicant.Id; approverId = approver.Id;

            var group = new AttendanceGroup { GroupName = "白班组辛" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            applicant.AttendanceGroupId = group.Id;
            db.AttendanceGroupApprovers.Add(new AttendanceGroupApprover { AttendanceGroupId = group.Id, UserId = approverId });
            db.SaveChanges();
        }

        // 走真实的提交流程（ApprovalService.SubmitApprovalAsync），这样才会真的发出"您有新的待审批申请"
        // 那条初始通知，跟直接往库里插申请单不是一回事
        using (var db = CreateContext())
        {
            var svc = new ApprovalService(db, new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance), AppOptions);
            var start = DateTime.Now.AddHours(1);
            await svc.SubmitApprovalAsync(applicantId, new SubmitApprovalDto
            {
                ApprovalType = ApprovalType.Leave, LeaveType = AttendanceSystem.Models.Enums.LeaveType.PersonalLeave,
                LeaveStartTime = start, LeaveEndTime = start.AddHours(4), Reason = "t", ApproverUserId = approverId
            });
        }

        int requestId;
        using (var db = CreateContext())
        {
            var req = await db.ApprovalRequests.SingleAsync(r => r.ApplicantUserId == applicantId);
            requestId = req.Id;
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == approverId && n.RelatedId == requestId));   // 提交时那一条初始通知
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == approverId && n.RelatedId == requestId && !n.IsRead));   // 审批人一直没点开

            req.UpdatedAt = DateTime.Now.AddHours(-5);   // 模拟这张单挂了 5 小时都没人处理
            await db.SaveChangesAsync();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        var all = await check.Notifications.Where(n => n.UserId == approverId).ToListAsync();
        Assert.Equal(2, all.Count);                                                 // 提交时那条事件通知 + 新的汇总提醒
        Assert.Contains(all, n => n.Title == "审批提醒" && !n.IsRead && n.Content.Contains("1 张"));   // 提醒没有被"提交时那条还没读"挡住——这是前一版的 bug
        Assert.Contains(all, n => n.RelatedId == requestId && n.Title != "审批提醒" && !n.IsRead);     // 提交时那条事件通知原样保留，不被动
    }

    // ── 2026-10-06：汇总提醒（每人每轮一条）+ 清理 ───────────────────────────────

    private async Task RunCleanupOldReminderNotificationsAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AttendanceDbContext>(o => o.UseSqlite(_connection));
        using var provider = services.BuildServiceProvider();
        var svc = new AttendanceBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AttendanceBackgroundService>.Instance);
        var method = typeof(AttendanceBackgroundService).GetMethod("CleanupOldReminderNotificationsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [])!;
    }

    private static ApprovalRequest StaleRequest(string no, int applicantId) => new()
    {
        RequestNo = no, ApplicantUserId = applicantId, ApprovalType = ApprovalType.Overtime,
        ApprovalStatus = ApprovalStatus.Pending, UpdatedAt = DateTime.Now.AddHours(-5)
    };

    [Fact]
    public async Task 待审批提醒_一个审批人压着好几张单_只发一条汇总_不是每张单一条()
    {
        int busyId, otherId;
        using (var db = CreateContext())
        {
            var applicant = U("A20", "申请人壬");
            var busy      = U("S20", "积压审批人");
            var other     = U("S21", "另一个审批人");
            db.Users.AddRange(applicant, busy, other);
            db.SaveChanges();
            busyId = busy.Id; otherId = other.Id;

            var reqs = new[] { StaleRequest("JB-AG-1", applicant.Id), StaleRequest("JB-AG-2", applicant.Id), StaleRequest("JB-AG-3", applicant.Id), StaleRequest("JB-AG-4", applicant.Id) };
            reqs[0].UpdatedAt = DateTime.Now.AddDays(-3);   // 最久的一张挂了 3 天
            db.ApprovalRequests.AddRange(reqs);
            db.SaveChanges();
            for (var i = 0; i < reqs.Length; i++)
                db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = reqs[i].Id, ApproverUserId = i < 3 ? busyId : otherId, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        var busyNotif = await check.Notifications.SingleAsync(n => n.UserId == busyId);   // 3 张单只有 1 条通知
        Assert.Contains("3 张", busyNotif.Content);
        Assert.Contains("3 天", busyNotif.Content);
        Assert.Equal("审批提醒", busyNotif.Title);
        Assert.Null(busyNotif.RelatedId);
        var otherNotif = await check.Notifications.SingleAsync(n => n.UserId == otherId);
        Assert.Contains("1 张", otherNotif.Content);
    }

    [Fact]
    public async Task 待审批提醒_改造前按单发的旧提醒和上一轮汇总_一律标成已读_提交时的事件通知不动()
    {
        int approverId;
        using (var db = CreateContext())
        {
            var applicant = U("A21", "申请人癸");
            var approver  = U("S22", "审批人癸");
            db.Users.AddRange(applicant, approver);
            db.SaveChanges();
            approverId = approver.Id;

            var req = StaleRequest("JB-LG-1", applicant.Id);
            db.ApprovalRequests.Add(req);
            db.SaveChanges();
            db.ApprovalSteps.Add(new ApprovalStep { ApprovalRequestId = req.Id, ApproverUserId = approverId, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending });
            db.Notifications.AddRange(
                new Notification { UserId = approverId, Title = "审批提醒", Content = "旧版按单发的提醒", NotificationType = "ApprovalPending", RelatedId = req.Id, CreatedAt = DateTime.Now.AddHours(-9) },
                new Notification { UserId = approverId, Title = "审批提醒", Content = "上一轮的汇总", NotificationType = "ApprovalPending", RelatedId = null, CreatedAt = DateTime.Now.AddHours(-4) },
                new Notification { UserId = approverId, Title = "您有新的待审批申请", Content = "提交时的事件通知", NotificationType = "ApprovalPending", RelatedId = req.Id, CreatedAt = DateTime.Now.AddHours(-5) });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();

        using var check = CreateContext();
        var all = await check.Notifications.Where(n => n.UserId == approverId).ToListAsync();
        Assert.Equal(4, all.Count);
        Assert.True(all.Single(n => n.Content == "旧版按单发的提醒").IsRead);
        Assert.True(all.Single(n => n.Content == "上一轮的汇总").IsRead);
        Assert.False(all.Single(n => n.Content == "提交时的事件通知").IsRead);   // 事件通知不是提醒，不该被动
        Assert.Single(all, n => n.Title == "审批提醒" && !n.IsRead);            // 提醒的未读始终只有最新这一条
    }

    [Fact]
    public async Task 待审批提醒_积压已经处理完_之前那条汇总被清成已读_不再发新的()
    {
        int approverId;
        using (var db = CreateContext())
        {
            var approver = U("S23", "积压清完的审批人");
            db.Users.Add(approver);
            db.SaveChanges();
            approverId = approver.Id;
            db.Notifications.Add(new Notification { UserId = approverId, Title = "审批提醒", Content = "您有 5 张待审批申请……", NotificationType = "ApprovalPending", CreatedAt = DateTime.Now.AddHours(-4) });
            db.SaveChanges();
        }

        await RunRemindPendingApprovalsAsync();   // 现在没有任何待审批申请了

        using var check = CreateContext();
        var only = await check.Notifications.SingleAsync(n => n.UserId == approverId);
        Assert.True(only.IsRead);   // 过时的汇总不该一直挂在未读里
    }

    [Fact]
    public async Task 审批提醒清理_只删7天前已读的提醒_未读的_较新的_事件通知都保留()
    {
        using (var db = CreateContext())
        {
            var u = U("S24", "清理对象");
            db.Users.Add(u);
            db.SaveChanges();
            var old = DateTime.Now.AddDays(-8);
            db.Notifications.AddRange(
                new Notification { UserId = u.Id, Title = "审批提醒", Content = "旧已读提醒", NotificationType = "ApprovalPending", IsRead = true, CreatedAt = old },
                new Notification { UserId = u.Id, Title = "审批提醒", Content = "旧未读提醒", NotificationType = "ApprovalPending", IsRead = false, CreatedAt = old },
                new Notification { UserId = u.Id, Title = "审批提醒", Content = "新已读提醒", NotificationType = "ApprovalPending", IsRead = true, CreatedAt = DateTime.Now.AddDays(-1) },
                new Notification { UserId = u.Id, Title = "您有新的待审批申请", Content = "旧已读事件通知", NotificationType = "ApprovalPending", IsRead = true, CreatedAt = old });
            db.SaveChanges();
        }

        await RunCleanupOldReminderNotificationsAsync();

        using var check = CreateContext();
        var left = (await check.Notifications.Select(n => n.Content).ToListAsync()).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "新已读提醒", "旧已读事件通知", "旧未读提醒" }.OrderBy(x => x).ToList(), left);
    }
}
