using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 员工考勤核对 / 异议（2026-11-03 新增）：7 天窗口、重复提交、通知范围（按分公司权限）、
/// 处理/驳回、撤回、以及"改完自动关单"。
/// </summary>
public class AttendanceDisputeTests : SqliteTestBase
{
    /// <summary>固定"现在 = 周二中午"，7 天窗口是 9/2 ~ 9/8。</summary>
    private static readonly DateTime NowAt = Tue.ToDateTime(new TimeOnly(12, 0));

    private (int emp, int admin, int clerkIn, int clerkOut, int deptId, int otherDeptId) SeedWorld()
    {
        using var db = CreateContext();
        var dept = new Department { DeptName = "测试部" };
        var otherDept = new Department { DeptName = "别的分公司" };
        db.Departments.AddRange(dept, otherDept);
        db.SaveChanges();

        var emp = U("E1", "员工甲"); emp.DepartmentId = dept.Id; emp.Role = UserRole.Employee;
        var admin = U("A1", "总部管理员"); admin.Role = UserRole.Admin;
        var clerkIn = U("C1", "本公司文员"); clerkIn.Role = UserRole.Clerk; clerkIn.ScopedDepartmentId = dept.Id;
        var clerkOut = U("C2", "别公司文员"); clerkOut.Role = UserRole.Clerk; clerkOut.ScopedDepartmentId = otherDept.Id;
        db.Users.AddRange(emp, admin, clerkIn, clerkOut);
        db.SaveChanges();

        db.AttendanceRecords.AddRange(
            new AttendanceRecord
            {
                UserId = emp.Id, WorkDate = Fri,
                ClockInTime = Fri.ToDateTime(new TimeOnly(8, 30)), ClockOutTime = Fri.ToDateTime(new TimeOnly(17, 30)),
                ActualWorkHours = 8m, AttendanceStatus = AttendanceStatus.Normal
            },
            new AttendanceRecord
            {
                UserId = emp.Id, WorkDate = Tue,
                ClockInTime = Tue.ToDateTime(new TimeOnly(9, 10)), ClockOutTime = Tue.ToDateTime(new TimeOnly(17, 30)),
                ActualWorkHours = 7.5m, AttendanceStatus = AttendanceStatus.Late, LateMinutes = 35
            });
        db.SaveChanges();
        return (emp.Id, admin.Id, clerkIn.Id, clerkOut.Id, dept.Id, otherDept.Id);
    }

    private AttendanceDisputeService Svc(AttendanceDbContext db) => new(db, new DeptScopeService(db), new FixedClock(NowAt));

    [Fact]
    public async Task 只能核对近7天_更早的不让提()
    {
        var (emp, _, _, _, _, _) = SeedWorld();
        using var db = CreateContext();
        var svc = Svc(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.SubmitAsync(emp, new DateOnly(2026, 8, 27), AttendanceDisputeType.MissingPunch, "那天下班卡没记上", null));
        Assert.Contains("7", ex.Message);

        Assert.True(await svc.SubmitAsync(emp, Fri, AttendanceDisputeType.OvertimeWrong, "周五加班 2 小时没算上", "应该是 2 小时") > 0);
    }

    [Fact]
    public async Task 同一天有处理中的异议不能重复提()
    {
        var (emp, _, _, _, _, _) = SeedWorld();
        using var db = CreateContext();
        var svc = Svc(db);
        await svc.SubmitAsync(emp, Tue, AttendanceDisputeType.WorkHoursWrong, "工时算少了半小时", null);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.SubmitAsync(emp, Tue, AttendanceDisputeType.WorkHoursWrong, "再提一次同样的", null));
        Assert.Contains("已经在处理中", ex.Message);
    }

    [Fact]
    public async Task 提交后通知管理员和本公司文员_不通知别家公司文员()
    {
        var (emp, admin, clerkIn, clerkOut, _, _) = SeedWorld();
        using (var db = CreateContext())
            await Svc(db).SubmitAsync(emp, Tue, AttendanceDisputeType.StatusWrong, "我那天只是迟到，不该记旷工", null);

        using var chk = CreateContext();
        var notices = await chk.Notifications.Where(n => n.NotificationType == NotificationTypes.AttendanceDispute).ToListAsync();
        Assert.Contains(notices, n => n.UserId == admin);
        Assert.Contains(notices, n => n.UserId == clerkIn);
        Assert.DoesNotContain(notices, n => n.UserId == clerkOut);
    }

    [Fact]
    public async Task 处理时校验分公司权限_处理后通知员工()
    {
        var (emp, _, clerkIn, clerkOut, deptId, otherDeptId) = SeedWorld();
        int id;
        using (var db = CreateContext())
            id = await Svc(db).SubmitAsync(emp, Tue, AttendanceDisputeType.WorkHoursWrong, "工时少算了 1 小时", null);

        using (var db = CreateContext())   // 别家公司的文员：只有别的部门范围 → 无权处理
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                Svc(db).HandleAsync(id, clerkOut, "别公司文员", true, "同意", new HashSet<int> { otherDeptId }));
            Assert.Contains("无权", ex.Message);
        }

        using (var db = CreateContext())   // 本公司文员（范围内）→ 正常处理
            await Svc(db).HandleAsync(id, clerkIn, "本公司文员", true, "已按加班单补上 1 小时", new HashSet<int> { deptId });

        using var chk = CreateContext();
        var d = await chk.AttendanceDisputes.SingleAsync(x => x.Id == id);
        Assert.Equal(AttendanceDisputeStatus.Resolved, d.Status);
        Assert.Equal("已按加班单补上 1 小时", d.Reply);
        Assert.Equal("本公司文员", d.HandlerName);
        var empNotices = await chk.Notifications.Where(n => n.UserId == emp && n.NotificationType == NotificationTypes.AttendanceDispute).ToListAsync();
        Assert.Contains(empNotices, n => n.Title.Contains("已处理"));
    }

    [Fact]
    public async Task 撤回后状态是已撤回_再撤回报错()
    {
        var (emp, _, _, _, _, _) = SeedWorld();
        int id;
        using (var db = CreateContext())
            id = await Svc(db).SubmitAsync(emp, Tue, AttendanceDisputeType.Other, "先问一下这天的记录", null);

        using (var db = CreateContext())
            await Svc(db).CancelAsync(emp, id);

        using (var chk = CreateContext())
            Assert.Equal(AttendanceDisputeStatus.Cancelled, (await chk.AttendanceDisputes.SingleAsync(x => x.Id == id)).Status);

        using (var db = CreateContext())
            await Assert.ThrowsAsync<BusinessException>(() => Svc(db).CancelAsync(emp, id));
    }

    [Fact]
    public async Task 核对列表只列近7天里有记录或有排班的日子()
    {
        var (emp, _, _, _, _, _) = SeedWorld();
        using var db = CreateContext();
        var rows = await Svc(db).GetVerifyListAsync(emp);

        Assert.All(rows, r => Assert.True(r.WorkDate >= Tue.AddDays(-6) && r.WorkDate <= Tue));
        Assert.Contains(rows, r => r.WorkDate == Tue && r.HasRecord);
        Assert.DoesNotContain(rows, r => r.WorkDate == Tue.AddDays(-10));
    }

    [Fact]
    public async Task 手动补卡后自动关单并通知员工()
    {
        var (emp, _, _, _, _, _) = SeedWorld();
        int id;
        using (var db = CreateContext())
            id = await Svc(db).SubmitAsync(emp, Tue, AttendanceDisputeType.WorkHoursWrong, "下班卡被记成 17:30 了", null);

        using (var db = CreateContext())
            Assert.Equal(1, await Svc(db).AutoResolveAsync(emp, Tue, "已手动补卡调整"));

        using var chk = CreateContext();
        var d = await chk.AttendanceDisputes.SingleAsync(x => x.Id == id);
        Assert.Equal(AttendanceDisputeStatus.Resolved, d.Status);
        Assert.Equal("系统", d.HandlerName);
        Assert.Contains(await chk.Notifications.Where(n => n.UserId == emp).ToListAsync(), n => n.Content.Contains("已调整"));
    }
}
