using System.Reflection;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.BackgroundServices;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-09-24 月度报表核对后的三条规则（都是用户拍板的）：
/// ① 免考勤的账号不自动记旷工/未打卡，报表也不统计他们的旷工；
/// ② 设备同步：当天第一次打卡如果晚于班次下班时间，按"下班卡"处理（缺上班卡），不再当成上班卡算出几百分钟迟到；
/// ③ 后台：只有下班卡没有上班卡 → "未打卡（缺上班卡）"，不算旷工。
/// </summary>
public class AbsentAndPunchRuleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());
    private static readonly DateOnly Tue = new(2026, 9, 8);   // 周二，普通工作日

    public AbsentAndPunchRuleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    // ── ② 设备同步：首次打卡晚于下班时间 → 下班卡 ──────────────────────────

    private static ShiftSchedule DayShift(bool cross = false) => new()
    {
        ShiftName = "白班", WorkStartTime = new TimeOnly(8, 30), WorkEndTime = new TimeOnly(17, 30),
        LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, IsCrossDay = cross, StandardWorkHours = 8
    };

    [Theory]
    [InlineData(22, 1, true)]     // 22:01：下班时间之后
    [InlineData(17, 30, true)]    // 正好 17:30：算下班时间之后
    [InlineData(17, 29, false)]
    [InlineData(8, 22, false)]    // 正常早上打卡
    [InlineData(12, 57, false)]   // 中午到岗（迟到）仍然是上班卡
    public void 首次打卡是否晚于下班时间(int h, int m, bool expected)
        => Assert.Equal(expected, AttendanceService.IsFirstPunchAfterShiftEnd(Tue, Tue.ToDateTime(new TimeOnly(h, m)), DayShift(), false));

    [Fact]
    public void 夜班_没排班_休息日_都不做这个判断()
    {
        var late = Tue.ToDateTime(new TimeOnly(22, 0));
        Assert.False(AttendanceService.IsFirstPunchAfterShiftEnd(Tue, late, DayShift(cross: true), false));   // 夜班晚上首次打卡本来就是上班
        Assert.False(AttendanceService.IsFirstPunchAfterShiftEnd(Tue, late, null, false));
        Assert.False(AttendanceService.IsFirstPunchAfterShiftEnd(Tue, late, DayShift(), isRestDay: true));
    }

    private (int userId, int shiftId) SeedDeviceWorld(string sn = "SN1")
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "生产组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = DayShift(); shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "E1", RealName = "员工甲", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 9, 1) };
        db.Users.Add(user);
        var dev = new ZKDevice { SN = sn, IsActive = true };
        db.ZKDevices.Add(dev);
        db.SaveChanges();
        db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
        foreach (var d in new[] { Tue, Tue.AddDays(1) })
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return (user.Id, shift.Id);
    }

    private ZKDeviceSyncService SyncSvc(AttendanceDbContext db)
    {
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        return new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
    }

    [Fact]
    public async Task 设备同步_当天唯一一次打卡在22点_按下班卡处理_不算迟到()
    {
        var (uid, _) = SeedDeviceWorld();
        using (var db = CreateContext())
            await SyncSvc(db).ProcessAttLogAsync("SN1", [new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(22, 1)), 0, 15)]);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Null(rec.ClockInTime);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(22, 1)), rec.ClockOutTime);
        Assert.Equal(0, rec.LateMinutes);                                   // 以前这里是 811
        var punch = await check.AttendancePunches.SingleAsync(p => p.UserId == uid);
        Assert.Equal(PunchType.ClockOut, punch.PunchType);
    }

    [Fact]
    public async Task 设备同步_正常早晚两次打卡_仍然是上班加下班_迟到照算()
    {
        var (uid, _) = SeedDeviceWorld();
        using (var db = CreateContext())
            await SyncSvc(db).ProcessAttLogAsync("SN1", [
                new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(12, 57)), 0, 15),   // 中午才到：上班卡，迟到
                new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(21, 0)), 0, 15)]);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(12, 57)), rec.ClockInTime);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(21, 0)), rec.ClockOutTime);
        Assert.Equal(AttendanceStatus.Late, rec.AttendanceStatus);
        Assert.Equal(267, rec.LateMinutes);
    }

    // ── ①③ 后台记旷工 ────────────────────────────────────────────────────

    private async Task RunMarkAbsentAsync(DateOnly day)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AttendanceDbContext>(o => o.UseSqlite(_connection));
        using var provider = services.BuildServiceProvider();
        var svc = new AttendanceBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AttendanceBackgroundService>.Instance);
        var method = typeof(AttendanceBackgroundService).GetMethod("MarkAbsentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [day])!;
    }

    private static User U(string no, string name, bool exempt = false) => new()
    {
        EmployeeNo = no, RealName = name, PasswordHash = "x", IsActive = true,
        HireDate = new DateOnly(2026, 9, 1), IsAttendanceExempt = exempt
    };

    [Fact]
    public async Task 后台记旷工_免考勤的人不记_普通人照记并发提醒()
    {
        int exemptId, normalId;
        using (var db = CreateContext())
        {
            var exempt = U("A1", "管理员甲", exempt: true); var normal = U("N1", "员工乙");
            db.Users.AddRange(exempt, normal);
            db.SaveChanges();
            exemptId = exempt.Id; normalId = normal.Id;
        }

        await RunMarkAbsentAsync(Tue);

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == exemptId));                 // 免考勤：没有任何记录
        Assert.Equal(AttendanceStatus.Absent, (await check.AttendanceRecords.SingleAsync(r => r.UserId == normalId)).AttendanceStatus);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.UserId == normalId));
        Assert.Equal(0, await check.Notifications.CountAsync(n => n.UserId == exemptId));
    }

    [Fact]
    public async Task 后台记旷工_只有下班卡没有上班卡_记未打卡不记旷工_重复跑不重复提醒()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("N2", "员工丙");
            db.Users.Add(u); db.SaveChanges();
            uid = u.Id;
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = Tue, ClockOutTime = Tue.ToDateTime(new TimeOnly(22, 1)), AttendanceStatus = AttendanceStatus.Normal
            });
            db.SaveChanges();
        }

        await RunMarkAbsentAsync(Tue);
        await RunMarkAbsentAsync(Tue);   // 补跑/重启后重复执行

        using var check = CreateContext();
        Assert.Equal(AttendanceStatus.NotPunched, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid)).AttendanceStatus);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.UserId == uid));
    }

    // ── ① 报表不统计免考勤的旷工 ─────────────────────────────────────────

    [Fact]
    public async Task 模板汇总表_免考勤的人旷工天数为0_普通人照常统计()
    {
        using (var db = CreateContext())
        {
            var exempt = U("A2", "管理员乙", exempt: true); var normal = U("N3", "员工丁");
            db.Users.AddRange(exempt, normal);
            db.SaveChanges();
            foreach (var u in new[] { exempt, normal })
                for (var i = 0; i < 3; i++)
                    db.AttendanceRecords.Add(new AttendanceRecord { UserId = u.Id, WorkDate = Tue.AddDays(i), AttendanceStatus = AttendanceStatus.Absent });
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var report = await svc.GenerateTemplateReportAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), null);

        Assert.Equal(0, report.Rows.Single(r => r.EmployeeNo == "A2").AbsentDays);
        Assert.Equal(3, report.Rows.Single(r => r.EmployeeNo == "N3").AbsentDays);
    }

    // ── 2026-09-24 审查补漏：④ 下班卡晚到不再停在旷工；⑤ 免考勤进看板口径、应出勤天数为 0 ─────────

    [Fact]
    public async Task 设备同步_23点55已被标旷工_之后补传的下班卡_旷工改成未打卡()
    {
        var (uid, _) = SeedDeviceWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, AttendanceStatus = AttendanceStatus.Absent });
            db.SaveChanges();
        }
        using (var db = CreateContext())
            await SyncSvc(db).ProcessAttLogAsync("SN1", [new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(22, 1)), 0, 15)]);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Null(rec.ClockInTime);
        Assert.Equal(AttendanceStatus.NotPunched, rec.AttendanceStatus);   // 以前一直停在"旷工"
    }

    [Fact]
    public async Task 设备同步_旷工的人补传了上班卡_照旧被纠正回来_不受影响()
    {
        var (uid, _) = SeedDeviceWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, AttendanceStatus = AttendanceStatus.Absent });
            db.SaveChanges();
        }
        using (var db = CreateContext())
            await SyncSvc(db).ProcessAttLogAsync("SN1", [new ZKAttLogRow("E1", Tue.ToDateTime(new TimeOnly(8, 25)), 0, 15)]);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(AttendanceStatus.Normal, rec.AttendanceStatus);
    }

    [Fact]
    public async Task 看板_免考勤的人不进总人数和未打卡名单()
    {
        int normalId;
        using (var db = CreateContext())
        {
            var exempt = U("A3", "管理员丙", exempt: true); var normal = U("N4", "员工戊");
            db.Users.AddRange(exempt, normal);
            db.SaveChanges();
            normalId = normal.Id;
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var stats = await svc.GetTodayStatsAsync();
        Assert.Equal(1, stats.TotalEmployees);
        Assert.Equal(1, stats.NotPunchedCount);

        var list = await svc.GetTodayStatsDetailAsync("notpunched");
        Assert.Equal([normalId], list.Select(r => r.UserId).ToList());          // 名单里只有需要打卡的人，且条数 = 卡片数字
        Assert.Equal(stats.NotPunchedCount, list.Count);
        Assert.Single(await svc.GetTodayStatsDetailAsync("total"));
    }

    [Fact]
    public async Task 应出勤天数_免考勤的人为0_普通人照常算_模板表和月度汇总一致()
    {
        using (var db = CreateContext())
        {
            db.Users.AddRange(U("A4", "管理员丁", exempt: true), U("N5", "员工己"));
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var report = await svc.GenerateTemplateReportAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), null);
        Assert.Equal(0, report.Rows.Single(r => r.EmployeeNo == "A4").ExpectedWorkdays);
        Assert.True(report.Rows.Single(r => r.EmployeeNo == "N5").ExpectedWorkdays > 0);

        await svc.GenerateMonthlySummaryAsync(2026, 9, null);
        using var check = CreateContext();
        var sums = await check.MonthlyAttendanceSummaries.Include(m => m.User).ToListAsync();
        Assert.Equal(0, sums.Single(m => m.User.EmployeeNo == "A4").ExpectedWorkdays);
        Assert.Equal(report.Rows.Single(r => r.EmployeeNo == "N5").ExpectedWorkdays, sums.Single(m => m.User.EmployeeNo == "N5").ExpectedWorkdays);
    }

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

    // ── 阿里云人脸接口：超时类异常要计入熔断、也要重试（2026-10-04 早高峰实测没计入）──────────

    private static T CallFaceClientStatic<T>(string name, params object[] args)
    {
        var m = typeof(AliyunFaceClient).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (T)m.Invoke(null, args)!;
    }

    [Fact]
    public void 人脸接口_Tea_SDK超时抛的WebException_计入熔断也算可重试()
    {
        // 生产日志里 10/4 早上 1100 多次失败的真实异常就是这个：System.Net.WebException: operation is timeout
        var timeout = new System.Net.WebException("operation is timeout");
        Assert.True(CallFaceClientStatic<bool>("ShouldCountForCircuitBreaker", timeout));
        Assert.True(CallFaceClientStatic<bool>("IsRetryable", timeout, CancellationToken.None));
    }

    [Fact]
    public void 人脸接口_被限流_照常重试但不计入熔断()
    {
        // 生产日志里的真实异常：code: 400, 调用被限流(...当前QPS:3,QPS阈值:2)——只说明请求太密，不说明服务坏了
        var throttled = new Tea.TeaException(new Dictionary<string, object>
        {
            ["code"] = "Throttling.User", ["message"] = "调用被限流", ["data"] = new Dictionary<string, object> { ["statusCode"] = 400 },
        });
        Assert.True(CallFaceClientStatic<bool>("IsRetryable", throttled, CancellationToken.None));
        Assert.False(CallFaceClientStatic<bool>("ShouldCountForCircuitBreaker", throttled));
    }

    [Fact]
    public void 人脸接口_参数类错误不计入熔断_调用方自己取消的不重试()
    {
        var bad = new InvalidOperationException("参数错误");
        Assert.False(CallFaceClientStatic<bool>("ShouldCountForCircuitBreaker", bad));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(CallFaceClientStatic<bool>("IsRetryable", new System.Net.WebException("operation is timeout"), cts.Token));   // 调用方已经取消，不再白花调用量
    }
}
