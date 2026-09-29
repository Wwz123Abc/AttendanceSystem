using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-09-24 第 11 轮审查修复的回归测试：请假按班次算 + 休息日规则、出差不含休息日、夜班续接时间窗、补一边卡的旷工、
/// 审批单号不撞号、登录锁定到期清零、管理账号的角色层级、全局异常只放行本程序的业务提示。
/// </summary>
public class Round11FixTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());

    private static readonly DateOnly Fri = new(2026, 9, 4);   // 周五
    private static readonly DateOnly Sat = new(2026, 9, 5);
    private static readonly DateOnly Sun = new(2026, 9, 6);
    private static readonly DateOnly Mon = new(2026, 9, 7);
    private static readonly DateOnly Tue = new(2026, 9, 8);

    public Round11FixTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    private static ShiftSchedule DayShift() => new()
    {
        ShiftName = "白班", WorkStartTime = new TimeOnly(8, 30), WorkEndTime = new TimeOnly(17, 30),
        LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 8   // 默认休息日是周日、周六
    };

    private static ShiftSchedule NightShift() => new()
    {
        ShiftName = "夜班", WorkStartTime = new TimeOnly(20, 0), WorkEndTime = new TimeOnly(8, 0), IsCrossDay = true,
        LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 11, RestDaysOfWeek = ""
    };

    // ── ① 请假时长按班次算 ─────────────────────────────────────────────

    [Fact]
    public void 请假_周一下午到周二中午_按班次时间算_合计约一天_不再是两天()
    {
        var shift = DayShift();
        var start = Mon.ToDateTime(new TimeOnly(13, 30));
        var end   = Tue.ToDateTime(new TimeOnly(12, 0));
        Assert.Equal(4m,   AttendanceService.ComputeLeaveHoursForDay(Mon, start, end, 60, 30, 8, shift));   // 13:30~17:30
        Assert.Equal(3.5m, AttendanceService.ComputeLeaveHoursForDay(Tue, start, end, 60, 30, 8, shift));   // 08:30~12:00
        // 没排班的日子没有班次可参照，仍按自然日算（老口径，封顶在标准工时）
        Assert.Equal(8m, AttendanceService.ComputeLeaveHoursForDay(Mon, start, end, 60, 30, 8));
    }

    [Fact]
    public void 请假_结束时间早于班次上班时间_当天没有交集()
    {
        var shift = DayShift();
        var start = Mon.ToDateTime(new TimeOnly(13, 30));
        var end   = Tue.ToDateTime(new TimeOnly(8, 0));   // 周二 08:00 结束，班次 08:30 才开始
        Assert.False(AttendanceService.HasLeaveOverlapForDay(Tue, start, end, shift));
        Assert.True(AttendanceService.HasLeaveOverlapForDay(Mon, start, end, shift));
    }

    [Fact]
    public void 请假_夜班请一晚_只落在当晚那一天()
    {
        var shift = NightShift();
        var start = Mon.ToDateTime(new TimeOnly(20, 0));
        var end   = Tue.ToDateTime(new TimeOnly(8, 0));
        Assert.Equal(10.5m, AttendanceService.ComputeLeaveHoursForDay(Mon, start, end, 60, 30, 11, shift));
        Assert.False(AttendanceService.HasLeaveOverlapForDay(Tue, start, end, shift));   // 周二排的是周二晚上的班，跟这次假不重叠
    }

    [Fact]
    public void 非工作日判断_休息日_法定节假日算_调班补班日不算()
    {
        var shift = DayShift();
        var legal = new Holiday { HolidayName = "国庆", HolidayDate = Tue, HolidayType = HolidayType.LegalHoliday };
        var comp  = new Holiday { HolidayName = "补班", HolidayDate = Sat, HolidayType = HolidayType.CompensatoryWorkDay };

        Assert.True(AttendanceService.IsNonWorkday(Sat, null, [], shift));          // 每周休息日
        Assert.False(AttendanceService.IsNonWorkday(Mon, null, [], shift));
        Assert.True(AttendanceService.IsNonWorkday(Tue, null, [legal], shift));     // 法定节假日
        Assert.False(AttendanceService.IsNonWorkday(Sat, null, [comp], shift));     // 周六补班：算工作日
        Assert.True(AttendanceService.IsNonWorkday(Sun, null, [], null));           // 没排班：周六周日兜底
    }

    [Theory]
    [InlineData(LeaveType.PersonalLeave, false)]
    [InlineData(LeaveType.SickLeave, false)]
    [InlineData(LeaveType.AnnualLeave, false)]
    [InlineData(LeaveType.CompensatoryLeave, false)]
    [InlineData(LeaveType.MarriageLeave, true)]
    [InlineData(LeaveType.MaternityLeave, true)]
    [InlineData(LeaveType.BereavementLeave, true)]
    public void 婚假产假丧假按自然日_其余假别只算工作日(LeaveType type, bool natural)
        => Assert.Equal(natural, AttendanceService.LeaveCountsNaturalDays(type));

    private (int userId, int groupId) SeedWeekWorld()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = DayShift(); shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "L1", RealName = "员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        db.SaveChanges();
        foreach (var d in new[] { Fri, Sat, Sun, Mon })   // 周末也排班（周末只是班次配置的休息日）
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return (user.Id, group.Id);
    }

    private async Task<int> AddApprovedAsync(ApprovalRequest r)
    {
        using var db = CreateContext();
        r.ApprovalStatus = ApprovalStatus.Approved;
        db.ApprovalRequests.Add(r);
        await db.SaveChangesAsync();
        return r.Id;
    }

    [Theory]
    [InlineData(LeaveType.PersonalLeave, false)]   // 事假：周末不算请假
    [InlineData(LeaveType.MarriageLeave, true)]    // 婚假：按自然日，周末也算
    public async Task 审批通过回写_跨周末请假_事假不含周末_婚假含周末(LeaveType type, bool weekendCounts)
    {
        var (uid, _) = SeedWeekWorld();
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "QJ-T-" + type, ApplicantUserId = uid, ApprovalType = ApprovalType.Leave, LeaveType = type,
            LeaveStartTime = Fri.ToDateTime(new TimeOnly(13, 30)), LeaveEndTime = Mon.ToDateTime(new TimeOnly(12, 0)), Reason = "t"
        });

        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);

        using var check = CreateContext();
        var recs = await check.AttendanceRecords.Where(r => r.UserId == uid).ToDictionaryAsync(r => r.WorkDate);
        Assert.Equal(4m,   recs[Fri].LeaveHours);   // 周五 13:30~17:30，不是"一直算到午夜 = 整天"
        Assert.Equal(3.5m, recs[Mon].LeaveHours);   // 周一 08:30~12:00，不是"从 0 点起 = 整天"
        Assert.Equal(AttendanceStatus.OnLeave, recs[Fri].AttendanceStatus);
        Assert.Equal(weekendCounts, recs.ContainsKey(Sat));
        Assert.Equal(weekendCounts, recs.ContainsKey(Sun));
        if (weekendCounts) Assert.Equal(8m, recs[Sat].LeaveHours);
    }

    [Fact]
    public async Task 审批通过回写_出差跨周末_周末不算出差()
    {
        var (uid, _) = SeedWeekWorld();
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "CC-T-1", ApplicantUserId = uid, ApprovalType = ApprovalType.BusinessTrip,
            BusinessTripStartTime = Fri.ToDateTime(new TimeOnly(8, 30)), BusinessTripEndTime = Mon.ToDateTime(new TimeOnly(17, 30)), Reason = "t"
        });

        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);

        using var check = CreateContext();
        var days = await check.AttendanceRecords.Where(r => r.UserId == uid && r.AttendanceStatus == AttendanceStatus.BusinessTrip)
            .Select(r => r.WorkDate).ToListAsync();
        Assert.Equivalent(new[] { Fri, Mon }, days);
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && (r.WorkDate == Sat || r.WorkDate == Sun)));
    }

    // ── ② 夜班续接时间窗 ─────────────────────────────────────────────

    private (int userId, int shiftId) SeedNightWorld()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "夜班组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = NightShift(); shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "N1", RealName = "夜班员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        var dev = new ZKDevice { SN = "SNN", IsActive = true };
        db.ZKDevices.Add(dev);
        db.SaveChanges();
        db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
        foreach (var d in new[] { Mon, Tue, Tue.AddDays(1) })
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
        // 周一晚上 20:00 上班，还没打下班卡
        db.AttendanceRecords.Add(new AttendanceRecord { UserId = user.Id, WorkDate = Mon, ClockInTime = Mon.ToDateTime(new TimeOnly(20, 0)), AttendanceStatus = AttendanceStatus.Normal });
        db.SaveChanges();
        return (user.Id, shift.Id);
    }

    private async Task SyncAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await svc.ProcessAttLogAsync("SNN", times.Select(t => new ZKAttLogRow("N1", t, 0, 15)).ToList());
    }

    [Fact]
    public async Task 夜班_周二早上的下班卡_仍然接到周一那条记录()
    {
        var (uid, _) = SeedNightWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 10)));

        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(8, 10)), mon.ClockOutTime);
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
    }

    [Fact]
    public async Task 夜班_周一漏打下班卡_周二晚上的上班卡不再被当成周一的下班卡()
    {
        var (uid, _) = SeedNightWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(20, 5)));   // 离周一应下班（周二 08:00）已经 12 个小时

        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Null(mon.ClockOutTime);                           // 以前这里会被填上 20:05
        var tue = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(20, 5)), tue.ClockInTime);   // 周二自己的上班卡
    }

    [Fact]
    public async Task 夜班_下班卡打完几分钟内又刷一次_仍归周一_不会凭空多出周二的上班卡()
    {
        var (uid, _) = SeedNightWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 0)));     // 第一次：下班
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 10)));    // 第二次：几分钟后又刷了一次

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(8, 10)), mon.ClockOutTime);   // 取更晚的一次
    }

    [Fact]
    public void 夜班续接时间窗_下班时间后6小时内算昨天_之后不算()
    {
        var shift = NightShift();   // 周一夜班，周二 08:00 下班
        Assert.True(AttendanceService.IsWithinNightCarryOver(Mon, shift, Tue.ToDateTime(new TimeOnly(14, 0))));
        Assert.False(AttendanceService.IsWithinNightCarryOver(Mon, shift, Tue.ToDateTime(new TimeOnly(14, 1))));
    }

    // ── ③ 只补一边卡的旷工 ─────────────────────────────────────────────

    [Theory]
    [InlineData(true)]    // 只补上班卡
    [InlineData(false)]   // 只补下班卡
    public async Task 手动补卡_旷工日只补一边_状态改成未打卡_不再挂旷工(bool onlyClockIn)
    {
        var (uid, _) = SeedWeekWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Mon, AttendanceStatus = AttendanceStatus.Absent });
            db.SaveChanges();
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var t = Mon.ToDateTime(new TimeOnly(onlyClockIn ? 8 : 17, 30));
            await svc.AdminAdjustPunchAsync(uid, Mon, onlyClockIn ? t : null, onlyClockIn ? null : t, null, "管理员");
        }
        using var check = CreateContext();
        Assert.Equal(AttendanceStatus.NotPunched, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon)).AttendanceStatus);
    }

    // ── ④ 审批单号 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 审批单号_当天前面的单被删过_也不会撞号()
    {
        var (uid, _) = SeedWeekWorld();
        var head = "QJ" + DateTime.Now.ToString("yyyyMMdd");
        using (var db = CreateContext())
        {
            // 当天只剩 0002（0001 已被删除）：按"条数+1"算出来是 0002，一直撞号
            db.ApprovalRequests.Add(new ApprovalRequest { RequestNo = head + "0002", ApplicantUserId = uid, ApprovalType = ApprovalType.Leave, Reason = "t" });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var svc = new ApprovalService(db2, new FakeAttendanceService(), AppOptions);
        Assert.Equal(head + "0003", await svc.GenerateRequestNoAsync(ApprovalType.Leave));
        Assert.Equal("BK" + DateTime.Now.ToString("yyyyMMdd") + "0001", await svc.GenerateRequestNoAsync(ApprovalType.PunchReplenishment));   // 别的类型各数各的
    }

    [Fact]
    public async Task 加班申请_单次超过24小时_提交时被拒绝()
    {
        var (uid, _) = SeedWeekWorld();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Today.AddHours(8);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Overtime, Reason = "t", OvertimeStartTime = start, OvertimeEndTime = start.AddHours(25)
        }));
        Assert.Contains("24", ex.Message);
    }

    // ── ⑤ 登录锁定到期后失败次数清零 ───────────────────────────────────

    [Fact]
    public async Task 登录锁定到期后_输错一次只算第一次失败_不会立刻再锁()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = new User { EmployeeNo = "LK1", RealName = "锁定测试", PasswordHash = UserService.HashPassword("Right#123"), IsActive = true,
                FailedLoginCount = 5, LockedUntil = DateTime.Now.AddMinutes(-1) };   // 上一轮锁定刚刚到期
            db.Users.Add(u); db.SaveChanges(); uid = u.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new UserService(db, null!, AppOptions, new DeptScopeService(db), NullLogger<UserService>.Instance);
            Assert.Null(await svc.ValidateLoginAsync("LK1", "wrong"));
        }
        using var check = CreateContext();
        var after = await check.Users.SingleAsync(u => u.Id == uid);
        Assert.Equal(1, after.FailedLoginCount);   // 以前是 6
        Assert.Null(after.LockedUntil);            // 以前立刻又被锁 15 分钟
    }

    // ── ⑥ 管理账号的角色层级 ───────────────────────────────────────────

    private static CurrentUser Cu(UserRole role, int? scope) => new() { UserId = 1, Role = role, ScopedDepartmentId = scope };

    [Fact]
    public void 角色层级_分公司账号不能操作范围为空的总部文员_总部账号不受影响()
    {
        var branchAdmin = Cu(UserRole.Admin, 10);
        var branchClerk = Cu(UserRole.Clerk, 10);
        var hqAdmin     = Cu(UserRole.Admin, null);
        var hqClerk     = Cu(UserRole.Clerk, null);

        Assert.False(branchAdmin.CanManageAccount(UserRole.Clerk, null));   // 范围为空的总部文员：分公司管理员不能动
        Assert.False(branchClerk.CanManageAccount(UserRole.Clerk, null));
        Assert.True(branchAdmin.CanManageAccount(UserRole.Clerk, 10));      // 有范围的分公司文员：可以
        Assert.True(branchAdmin.CanManageAccount(UserRole.Employee, null)); // 普通员工/主管/班组长不受影响
        Assert.True(branchAdmin.CanManageAccount(UserRole.Supervisor, null));
        Assert.True(hqAdmin.CanManageAccount(UserRole.Clerk, null));        // 总部管理员可以操作所有人
        Assert.True(hqClerk.CanManageAccount(UserRole.Clerk, null));        // 总部文员之间不受影响
        // 原有规则不变：总部管理员（Admin 且没范围）只有总部超管能动；分公司管理员之间可以互相操作
        Assert.False(branchAdmin.CanManageAccount(UserRole.Admin, null));
        Assert.False(hqClerk.CanManageAccount(UserRole.Admin, 10));
        Assert.True(branchAdmin.CanManageAccount(UserRole.Admin, 20));
    }

    // ── ⑦ 全局异常：只放行本程序自己抛出的业务提示 ─────────────────────

    [Fact]
    public async Task 异常来源判断_本程序抛出的算业务提示_框架抛出的不算()
    {
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var ours = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AdminAdjustPunchAsync(1, Mon, null, null, new string('x', 101), null));
        Assert.Equal(typeof(AttendanceService).Assembly, ours.TargetSite?.DeclaringType?.Assembly);

        var framework = Assert.Throws<InvalidOperationException>(() => new List<int>().First());   // 框架自己抛的同一个类型
        Assert.NotEqual(typeof(AttendanceService).Assembly, framework.TargetSite?.DeclaringType?.Assembly);
    }

    // ── ⑧ 自助登记：姓名为空（模型绑定给 null）时给出正确提示 ───────────

    [Fact]
    public async Task 自助登记_姓名为null_提示请填写姓名_而不是空引用()
    {
        using var db = CreateContext();
        var svc = new EmployeeRegistrationService(db, new DeptScopeService(db));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitAsync(new SubmitRegistrationDto
        {
            RealName = null!, Phone = "13800000000", IdNumber = "110101199001011234"
        }));
        Assert.Equal("请填写姓名", ex.Message);
    }

    // ── ⑨ 午间打卡：打完卡"我的记录"里就要有命中情况，不用等下班卡 ─────────

    private (int userId, int shiftId) SeedDayMidWorld()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "午卡组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = DayShift(); shift.AttendanceGroupId = group.Id;
        shift.MidCheckWindows = "12:00-13:00";
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "M1", RealName = "午卡员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        var dev = new ZKDevice { SN = "SNM", IsActive = true };
        db.ZKDevices.Add(dev);
        db.SaveChanges();
        db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = Tue, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return (user.Id, shift.Id);
    }

    [Fact]
    public async Task 考勤机同步_午间打了卡_下班卡还没打_我的记录里就有午间命中()
    {
        var (uid, _) = SeedDayMidWorld();
        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await svc.ProcessAttLogAsync("SNM", [
                new ZKAttLogRow("M1", Tue.ToDateTime(new TimeOnly(8, 25)), 0, 15),    // 上班
                new ZKAttLogRow("M1", Tue.ToDateTime(new TimeOnly(12, 30)), 0, 15)]); // 午间
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Null(rec.ClockOutTime);                                   // 午间卡不是下班卡
        var hits = rec.MidCheckResults.ParseMidCheckResults();
        var hit = Assert.Single(hits);
        Assert.Equal(new TimeOnly(12, 30), hit.HitTime);                 // 以前这里是 null，页面上一直显示 "--"
    }

    [Fact]
    public async Task 远程打卡_上班后打午间卡_立刻写回午间命中_不用等下班卡()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "远程午卡组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            // 窗口设成整天，保证不管测试什么时候跑，"现在"这一次打卡都落在窗口里
            var shift = DayShift(); shift.AttendanceGroupId = group.Id; shift.MidCheckWindows = "00:00-23:59";
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "M2", RealName = "远程员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = today, ShiftScheduleId = shift.Id });
            db.SaveChanges();
            uid = user.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            Assert.True((await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockIn }, skipLocationCheck: true)).Success);
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            Assert.True((await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.MidCheck }, skipLocationCheck: true)).Success);
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == today);
        Assert.Null(rec.ClockOutTime);
        Assert.NotNull(rec.MidCheckResults);
        Assert.True(rec.MidCheckResults.ParseMidCheckResults().Single().IsSatisfied);
    }

    // ── ⑩ 休息日不计正班工时，批了加班的只算加班（2026-09-28 用户确认）────────

    [Theory]
    [InlineData(13.5)]    // 休息日全天加班：正班 0，只有加班 13.5
    [InlineData(0)]       // 休息日没批加班：本来就是 0
    public async Task 手动补卡_休息日不计正班工时_有加班也一样(double otHours)
    {
        var (uid, _) = SeedWeekWorld();   // 周五~周一都排了白班，班次配置的每周休息日是周六周日
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Sat, OvertimeHours = (decimal)otHours });
            db.SaveChanges();
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.AdminAdjustPunchAsync(uid, Sat, Sat.ToDateTime(new TimeOnly(8, 24)), Sat.ToDateTime(new TimeOnly(22, 2)), null, "管理员");
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Sat);
        Assert.Equal(0m, rec.ActualWorkHours);                  // 以前有加班时这里是 8：正班 8 + 加班 13.5 重复计算
        Assert.Equal((decimal)otHours, rec.OvertimeHours);      // 加班时长以审批单为准，不受影响
    }

    [Fact]
    public async Task 手动补卡_工作日照常计正班工时_加班另算()
    {
        var (uid, _) = SeedWeekWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Mon, OvertimeHours = 4m });
            db.SaveChanges();
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.AdminAdjustPunchAsync(uid, Mon, Mon.ToDateTime(new TimeOnly(8, 25)), Mon.ToDateTime(new TimeOnly(22, 0)), null, "管理员");
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(8m, rec.ActualWorkHours);     // 工作日：正班封顶在应下班时间，8 小时
        Assert.Equal(4m, rec.OvertimeHours);
    }

    [Fact]
    public async Task 考勤机同步_休息日有加班_正班工时也是0_只有加班()
    {
        var (uid, _) = SeedDayMidWorld();
        using (var db = CreateContext())
        {
            // 把排班挪到周六（班次配置的每周休息日），并提前批好加班
            var assign = db.ShiftAssignments.Single(a => a.UserId == uid);
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = uid, WorkDate = Sat, ShiftScheduleId = assign.ShiftScheduleId });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Sat, OvertimeHours = 13.5m });
            db.SaveChanges();
        }
        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await svc.ProcessAttLogAsync("SNM", [
                new ZKAttLogRow("M1", Sat.ToDateTime(new TimeOnly(8, 24)), 0, 15),
                new ZKAttLogRow("M1", Sat.ToDateTime(new TimeOnly(22, 2)), 0, 15)]);
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Sat);
        Assert.NotNull(rec.ClockOutTime);
        Assert.Equal(0m, rec.ActualWorkHours);
        Assert.Equal(13.5m, rec.OvertimeHours);
    }

    // ── ⑪ 加班时长按申请的时间段扣饭点：超过 6 小时扣午休、超过 9 小时再扣晚餐（2026-09-28 用户确认）────

    [Theory]
    [InlineData(18, 0, 22, 0, 4)]       // 4 小时：不扣
    [InlineData(8, 30, 17, 30, 8)]      // 9 小时：超过 6 小时扣午休 60 分钟；正好 9 小时不算"超过 9 小时"，不扣晚餐
    [InlineData(8, 30, 22, 0, 12)]      // 13.5 小时（陈林发 9/26 那张）：扣 60 + 30 分钟 = 12 小时
    [InlineData(8, 0, 20, 0, 10.5)]     // 12 小时 → 10.5
    [InlineData(9, 0, 15, 0, 6)]        // 正好 6 小时：不算"超过 6 小时"，不扣
    public async Task 加班审批通过回写_申请时长超过6小时9小时按规则扣饭点(int sh, int sm, int eh, int em, double expected)
    {
        var (uid, _) = SeedWeekWorld();
        var start = Mon.ToDateTime(new TimeOnly(sh, sm));
        var end   = Mon.ToDateTime(new TimeOnly(eh, em));
        var raw   = (decimal)(end - start).TotalHours;
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = $"JB-T-{sh}{sm}-{eh}{em}", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            OvertimeStartTime = start, OvertimeEndTime = end, OvertimeDurationHours = raw,   // 单子上存的是不扣饭点的总长度（老单子/未回写时的样子）
            Reason = "t"
        });

        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal((decimal)expected, rec.OvertimeHours);
        Assert.Equal((decimal)expected, (await check.ApprovalRequests.SingleAsync(a => a.Id == id)).OvertimeDurationHours);   // 单子上的时长同步成实际记入的数
    }

    [Fact]
    public async Task 加班申请_不足半小时_提交时被拒绝()
    {
        var (uid, _) = SeedWeekWorld();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Today.AddHours(18);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Overtime, Reason = "t", OvertimeStartTime = start, OvertimeEndTime = start.AddMinutes(20)
        }));
        Assert.Contains("0.5", ex.Message);
    }

    // ── ⑫ 夜班上班卡合理性：下班后重复刷、打得太早（2026-09-28 线上 13 条夜班记录被弄乱）───────

    private async Task CloseMondayNightShiftAsync(DateTime clockOut)
    {
        using var db = CreateContext();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.WorkDate == Mon);
        rec.ClockOutTime = clockOut;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task 夜班刚打完下班卡_52秒后又点了一次_被拒绝_不生成新的上班卡()
    {
        var (uid, _) = SeedNightWorld();                                    // 周一 20:00 上班，周二 08:00 下班（跨天班次）
        await CloseMondayNightShiftAsync(Tue.ToDateTime(new TimeOnly(8, 40)));

        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var msg = await svc.GetClockInRejectionAsync(uid, Tue.ToDateTime(new TimeOnly(8, 40, 53)));
        Assert.NotNull(msg);
        Assert.Contains("刚刚", msg);
        Assert.Contains("08:40", msg);
    }

    [Theory]
    [InlineData(9, 30, true)]     // 下班后 50 分钟：不算"重复刷"，但离周二 20:00 上班还早 → 太早
    [InlineData(13, 59, true)]    // 上班时间前 6 小时零 1 分钟：太早
    [InlineData(14, 0, false)]    // 正好前 6 小时：可以
    [InlineData(19, 51, false)]   // 晚上来上班：可以（肖文城 9/27 那次）
    public async Task 跨天班次的上班卡_离应上班时间前6小时以外的被拒绝(int h, int m, bool rejected)
    {
        var (uid, _) = SeedNightWorld();
        await CloseMondayNightShiftAsync(Tue.ToDateTime(new TimeOnly(8, 0)));

        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var msg = await svc.GetClockInRejectionAsync(uid, Tue.ToDateTime(new TimeOnly(h, m)));
        Assert.Equal(rejected, msg is not null);
        if (rejected && (h, m) != (9, 30)) Assert.Contains("14:00", msg);   // 提示里写明最早几点可以打
    }

    [Fact]
    public async Task 白班的上班卡_不受这条限制()
    {
        var (uid, _) = SeedWeekWorld();   // 周五~周一白班 08:30 上班，不是跨天班次
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        Assert.Null(await svc.GetClockInRejectionAsync(uid, Mon.ToDateTime(new TimeOnly(5, 0))));   // 再早也不拦
    }

    [Fact]
    public async Task 今天已经有上班卡_不再拦截()
    {
        var (uid, _) = SeedNightWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, ClockInTime = Tue.ToDateTime(new TimeOnly(19, 40)) });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        Assert.Null(await svc.GetClockInRejectionAsync(uid, Tue.ToDateTime(new TimeOnly(9, 0))));
    }

    [Fact]
    public async Task 考勤机同步_跨天班次的第一次打卡离上班时间太早_不当上班卡_也不建记录()
    {
        var (uid, _) = SeedNightWorld();
        await CloseMondayNightShiftAsync(Tue.ToDateTime(new TimeOnly(8, 0)));
        await SyncAsync(Tue.ToDateTime(new TimeOnly(12, 0)));     // 周二中午（离 20:00 上班还有 8 小时）

        using (var check = CreateContext())
            Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));   // 以前这里会建一条 12:00 的上班卡

        await SyncAsync(Tue.ToDateTime(new TimeOnly(19, 40)));    // 晚上来上班：正常记上班卡
        using var check2 = CreateContext();
        var tue = await check2.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(19, 40)), tue.ClockInTime);
    }

    // ── ⑬ 工作日加班时间不能和当天班次的上下班时间重叠（2026-09-28 用户确认）────────────

    /// <summary>造一个"今天排了白班 08:30-17:30"的员工（有直属上级，提交才走得通）；restDays 是这个班次的每周休息日。</summary>
    private int SeedOvertimeWorld(string restDays, ShiftSchedule? shiftOverride = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "加班组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = shiftOverride ?? DayShift();
        shift.AttendanceGroupId = group.Id; shift.RestDaysOfWeek = restDays;
        db.ShiftSchedules.Add(shift);
        var boss = new User { EmployeeNo = "B1", RealName = "上级", PasswordHash = "x", IsActive = true, Role = UserRole.Supervisor };
        db.Users.Add(boss);
        db.SaveChanges();
        var user = new User { EmployeeNo = "OT1", RealName = "加班员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, SupervisorUserId = boss.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        db.SaveChanges();
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = today, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return user.Id;
    }

    private async Task<Exception?> TrySubmitOvertimeAsync(int uid, TimeOnly start, TimeOnly end)
    {
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var today = DateOnly.FromDateTime(DateTime.Today);
        try
        {
            await svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
            {
                ApprovalType = ApprovalType.Overtime, Reason = "t",
                OvertimeStartTime = today.ToDateTime(start), OvertimeEndTime = today.ToDateTime(end)
            });
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    [Theory]
    [InlineData(8, 30, 17, 30)]     // 整个班次都填成加班（线上 110 张就是这样）
    [InlineData(8, 30, 19, 30)]     // 上班时间开始，一直加班到晚上
    [InlineData(9, 0, 12, 0)]       // 完全落在班次里面
    [InlineData(7, 0, 9, 0)]        // 早上提前上班，但压到了班次开始之后（08:30~09:00）
    [InlineData(16, 0, 19, 0)]      // 下班前就开始填（16:00~17:30 重叠）
    public async Task 工作日加班_和班次上下班时间重叠_提交被拒绝(int sh, int sm, int eh, int em)
    {
        var uid = SeedOvertimeWorld(restDays: "");   // 没有每周休息日：今天肯定是工作日
        var ex = await TrySubmitOvertimeAsync(uid, new TimeOnly(sh, sm), new TimeOnly(eh, em));
        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.StartsWith("加班时间不能和上班时间重叠，请只填下班后（或上班前）的加班时段", ioe.Message);
        Assert.Contains("08:30–17:30", ioe.Message);            // 提示里带上当天的班次时间
    }

    [Theory]
    [InlineData(17, 30, 21, 0)]     // 正好从下班时间开始：不重叠
    [InlineData(18, 0, 22, 0)]      // 下班后
    [InlineData(5, 0, 8, 30)]       // 上班前，正好到上班时间：不重叠
    public async Task 工作日加班_只填下班后或上班前_不受影响(int sh, int sm, int eh, int em)
    {
        var uid = SeedOvertimeWorld(restDays: "");
        Assert.Null(await TrySubmitOvertimeAsync(uid, new TimeOnly(sh, sm), new TimeOnly(eh, em)));
    }

    [Fact]
    public async Task 休息日加班_整天都可以填_不受限制()
    {
        var todayDow = ((int)DateTime.Today.DayOfWeek).ToString();
        var uid = SeedOvertimeWorld(restDays: todayDow);            // 今天是这个班次的每周休息日
        Assert.Null(await TrySubmitOvertimeAsync(uid, new TimeOnly(8, 30), new TimeOnly(20, 0)));
    }

    [Fact]
    public async Task 法定节假日加班_整天都可以填_不受限制()
    {
        var uid = SeedOvertimeWorld(restDays: "");
        using (var db = CreateContext())
        {
            db.Holidays.Add(new Holiday { HolidayName = "国庆", HolidayDate = DateOnly.FromDateTime(DateTime.Today), HolidayType = HolidayType.LegalHoliday });
            db.SaveChanges();
        }
        Assert.Null(await TrySubmitOvertimeAsync(uid, new TimeOnly(8, 30), new TimeOnly(20, 0)));
    }

    [Fact]
    public async Task 调班补班日_算工作日_重叠照样拒绝()
    {
        var todayDow = ((int)DateTime.Today.DayOfWeek).ToString();
        var uid = SeedOvertimeWorld(restDays: todayDow);            // 本来是休息日，但今天是补班日
        using (var db = CreateContext())
        {
            db.Holidays.Add(new Holiday { HolidayName = "补班", HolidayDate = DateOnly.FromDateTime(DateTime.Today), HolidayType = HolidayType.CompensatoryWorkDay });
            db.SaveChanges();
        }
        Assert.IsType<InvalidOperationException>(await TrySubmitOvertimeAsync(uid, new TimeOnly(8, 30), new TimeOnly(17, 30)));
    }

    [Fact]
    public async Task 当天没排班_不判断重叠()
    {
        var uid = SeedOvertimeWorld(restDays: "");
        using (var db = CreateContext())
        {
            db.ShiftAssignments.RemoveRange(db.ShiftAssignments.Where(a => a.UserId == uid));
            db.SaveChanges();
        }
        Assert.Null(await TrySubmitOvertimeAsync(uid, new TimeOnly(8, 30), new TimeOnly(17, 30)));
    }

    // ── ⑭ 2026-09-28 全项目审查：夜班下班遇到"今天有空记录"、夜班延续段加班重叠、人脸文件丢失 ──────

    [Fact]
    public async Task 夜班下班_今天有一条请假的空记录_仍然接到昨天的夜班_不当成今天的上班卡()
    {
        var (uid, _) = SeedNightWorld();   // 周一 20:00 上班（没下班），周二 08:00 下班（跨天班次）
        using (var db = CreateContext())
        {
            // 周二请了假：审批通过时提前给周二建了一条"请假"的空记录（没有上班卡）
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, AttendanceStatus = AttendanceStatus.OnLeave, LeaveHours = 11 });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var rec = await svc.GetTodayAttendanceAsync(uid, Tue.ToDateTime(new TimeOnly(8, 40)));
        Assert.NotNull(rec);
        Assert.Equal(Mon, rec!.WorkDate);                       // 返回的是周一那条没下班的夜班记录，所以这次卡会被判成"下班"
        Assert.NotNull(rec.ClockInTime);
    }

    [Fact]
    public async Task 今天已经有上班卡_不再回头找昨天的记录()
    {
        var (uid, _) = SeedNightWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, ClockInTime = Tue.ToDateTime(new TimeOnly(19, 50)), AttendanceStatus = AttendanceStatus.Normal });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var rec = await svc.GetTodayAttendanceAsync(uid, Tue.ToDateTime(new TimeOnly(20, 30)));
        Assert.Equal(Tue, rec!.WorkDate);
    }

    [Theory]
    [InlineData(0, 0, 4, 0, true)]      // 凌晨 00:00~04:00：压在昨晚夜班（20:00~次日 08:00）的正班时间里 → 拒绝
    [InlineData(6, 0, 9, 0, true)]      // 早上 06:00~09:00：夜班 08:00 才下班，重叠 → 拒绝
    [InlineData(8, 0, 10, 0, false)]    // 08:00 起（夜班已下班）：不重叠 → 可以
    public async Task 夜班延续到今天凌晨的那一段_也不能填加班(int sh, int sm, int eh, int em, bool rejected)
    {
        var uid = SeedOvertimeWorld(restDays: "", NightShift());   // 20:00~次日 08:00 的夜班，先排在今天
        using (var db = CreateContext())
        {
            // 改成只排了"昨天"的夜班；今天没排——填的是夜班延续到今天早上的那一段
            var asg = db.ShiftAssignments.Single(x => x.UserId == uid);
            asg.WorkDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
            db.SaveChanges();
        }
        var ex = await TrySubmitOvertimeAsync(uid, new TimeOnly(sh, sm), new TimeOnly(eh, em));
        if (rejected)
        {
            var ioe = Assert.IsType<InvalidOperationException>(ex);
            Assert.StartsWith("加班时间不能和上班时间重叠", ioe.Message);
            Assert.Contains("前一天", ioe.Message);
        }
        else Assert.Null(ex);
    }

    private sealed class TempEnv(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "t";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void 人脸参考照文件是否还在_有文件才算_丢了允许重录_路径穿越不算()
    {
        var root = Path.Combine(Path.GetTempPath(), "att-face-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "PrivateUploads", "uploads", "faces", "E1");
        Directory.CreateDirectory(dir);
        try
        {
            var env = new TempEnv(root);
            Assert.False(Helpers.PrivateFileStorage.FaceReferenceFileExists(env, null));
            Assert.False(Helpers.PrivateFileStorage.FaceReferenceFileExists(env, "/uploads/faces/E1/ref.jpg"));   // 库里有地址、文件没有
            File.WriteAllText(Path.Combine(dir, "ref.jpg"), "x");
            Assert.True(Helpers.PrivateFileStorage.FaceReferenceFileExists(env, "/uploads/faces/E1/ref.jpg"));
            File.Delete(Path.Combine(dir, "ref.jpg"));
            File.WriteAllText(Path.Combine(dir, "ref_verify.jpg"), "x");                                          // 只剩瘦身版也算在
            Assert.True(Helpers.PrivateFileStorage.FaceReferenceFileExists(env, "/uploads/faces/E1/ref.jpg"));
            Assert.False(Helpers.PrivateFileStorage.FaceReferenceFileExists(env, "/uploads/../../secret.jpg"));   // 路径穿越
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── ⑮ 2026-09-28 第二轮审查：白班加班过零点、补卡跨天、休息日出勤、夜班天数、报表节假日、手动补卡校验 ──────

    private int SeedDayDeviceWorld()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "白班设备组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = DayShift(); shift.AttendanceGroupId = group.Id;   // 08:30-17:30，周日周六休息
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "D1", RealName = "白班员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        var dev = new ZKDevice { SN = "SND", IsActive = true };
        db.ZKDevices.Add(dev);
        db.SaveChanges();
        db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
        foreach (var d in new[] { Mon, Tue })
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
        // 周一 08:25 上班，加班中，还没下班
        db.AttendanceRecords.Add(new AttendanceRecord { UserId = user.Id, WorkDate = Mon, ClockInTime = Mon.ToDateTime(new TimeOnly(8, 25)), AttendanceStatus = AttendanceStatus.Normal });
        db.SaveChanges();
        return user.Id;
    }

    private async Task SyncDayAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await svc.ProcessAttLogAsync("SND", times.Select(t => new ZKAttLogRow("D1", t, 0, 15)).ToList());
    }

    [Fact]
    public async Task 白班加班过零点_周二00点40的下班卡_接到周一那条记录_周一有工时_周二不凭空多出记录()
    {
        var uid = SeedDayDeviceWorld();
        await SyncDayAsync(Tue.ToDateTime(new TimeOnly(0, 40)));

        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(0, 40)), mon.ClockOutTime);
        Assert.True(mon.ActualWorkHours > 0);                                            // 以前是 0
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
    }

    [Fact]
    public async Task 白班_周一忘打下班卡_周二早上8点28的卡仍然是周二的上班卡_不被吞到周一()
    {
        var uid = SeedDayDeviceWorld();
        await SyncDayAsync(Tue.ToDateTime(new TimeOnly(8, 28)));

        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Null(mon.ClockOutTime);
        var tue = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(8, 28)), tue.ClockInTime);
    }

    [Theory]
    [InlineData(0, 40, true)]     // 加班过零点
    [InlineData(2, 29, true)]     // 离今天 08:30 上班还有 6 小时零 1 分：不可能是今天的上班卡
    [InlineData(2, 31, false)]    // 离上班不到 6 小时：可能是今天提前来的上班卡
    [InlineData(8, 28, false)]
    public void 白班过零点下班的判断_只有比今天上班时间早6小时以上才算昨天的下班卡(int h, int m, bool expected)
    {
        var yesterdayIn = Mon.ToDateTime(new TimeOnly(8, 25));
        Assert.Equal(expected, AttendanceService.IsPostMidnightClockOutOfDayShift(Tue.ToDateTime(new TimeOnly(h, m)), yesterdayIn, DayShift(), DayShift()));
    }

    [Fact]
    public void 白班过零点下班的判断_夜班走原来的续接_离昨天上班卡太久的也不算()
    {
        var yesterdayIn = Mon.ToDateTime(new TimeOnly(8, 25));
        Assert.False(AttendanceService.IsPostMidnightClockOutOfDayShift(Tue.ToDateTime(new TimeOnly(0, 40)), yesterdayIn, NightShift(), DayShift()));   // 昨天是夜班：不归这里
        Assert.False(AttendanceService.IsPostMidnightClockOutOfDayShift(Tue.ToDateTime(new TimeOnly(5, 0)), yesterdayIn, DayShift(), null));           // 离上班卡 20.6 小时，超过 20 小时上限
        Assert.True(AttendanceService.IsPostMidnightClockOutOfDayShift(Tue.ToDateTime(new TimeOnly(5, 0)), Mon.ToDateTime(new TimeOnly(9, 30)), DayShift(), null));   // 没排班：06:00 之前
    }

    [Fact]
    public async Task 白班加班过零点_我的今日打卡状态返回周一那条_下一次该打的是下班卡()
    {
        var uid = SeedDayDeviceWorld();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var rec = await svc.GetTodayAttendanceAsync(uid, Tue.ToDateTime(new TimeOnly(0, 40)));
        Assert.NotNull(rec);
        Assert.Equal(Mon, rec!.WorkDate);
        Assert.Null(await svc.GetTodayAttendanceAsync(uid, Tue.ToDateTime(new TimeOnly(8, 28))));   // 早上正常上班：今天还没有记录
    }

    [Fact]
    public async Task 补卡申请_夜班补下班卡填第二天早上的时间点_自动顺延到第二天_工时出现()
    {
        var (uid, _) = SeedNightWorld();   // 周一 20:00 上班，没下班
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "BK-T-1", ApplicantUserId = uid, ApprovalType = ApprovalType.PunchReplenishment,
            PunchDate = Mon, PunchType = PunchType.ClockOut, PunchTime = new TimeOnly(8, 0), Reason = "t"
        });
        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);

        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(8, 0)), mon.ClockOutTime);   // 不是"周一 08:00"（比上班还早、工时 0）
        Assert.True(mon.ActualWorkHours > 0);
    }

    [Fact]
    public void 补卡下班卡落在哪一天_有上班卡的按先后_没上班卡的跨天班次按班次上班时间判断()
    {
        var day = Mon;
        Assert.Equal(Mon.ToDateTime(new TimeOnly(17, 40)), AttendanceService.ResolvePunchReplenishmentClockOut(day, new TimeOnly(17, 40), Mon.ToDateTime(new TimeOnly(8, 30)), DayShift()));
        Assert.Equal(Tue.ToDateTime(new TimeOnly(8, 0)),   AttendanceService.ResolvePunchReplenishmentClockOut(day, new TimeOnly(8, 0),  Mon.ToDateTime(new TimeOnly(20, 0)), NightShift()));
        Assert.Equal(Tue.ToDateTime(new TimeOnly(8, 0)),   AttendanceService.ResolvePunchReplenishmentClockOut(day, new TimeOnly(8, 0),  null, NightShift()));      // 没上班卡，夜班：早于 20:00 → 第二天
        Assert.Equal(Mon.ToDateTime(new TimeOnly(22, 0)),  AttendanceService.ResolvePunchReplenishmentClockOut(day, new TimeOnly(22, 0), null, NightShift()));      // 晚于上班时间：当天
        Assert.Equal(Mon.ToDateTime(new TimeOnly(8, 0)),   AttendanceService.ResolvePunchReplenishmentClockOut(day, new TimeOnly(8, 0),  null, DayShift()));        // 白班没上班卡：不动
    }

    // ── H1（2026-09-29 第二轮审查·高）：没排班的人不该被这两条"跨天顺延"规则多算十几个小时 ──────

    [Fact]
    public void 完全没排班的人_昨天上班没打下班卡_今天凌晨的卡不会被强行接成昨天的下班卡()
    {
        var yesterdayIn = Mon.ToDateTime(new TimeOnly(10, 0));
        // 昨天、今天都没排班：以前只要没超过 20 小时、时间在 06:00 之前就会接，10:00 上班到次日 05:30 会被
        // 算成 18 小时的"班"；现在昨天/今天至少要有一天排了班（哪怕不是跨天班次）才继续判断
        Assert.False(AttendanceService.IsPostMidnightClockOutOfDayShift(Tue.ToDateTime(new TimeOnly(5, 30)), yesterdayIn, null, null));
    }

    [Fact]
    public void 排了班的人_同样场景仍然按原规则接续()
    {
        var yesterdayIn = Mon.ToDateTime(new TimeOnly(10, 0));
        // 昨天排了班（哪怕不是跨天班次）：不受这次收紧影响，照常判断"凌晨 06:00 之前"这条规则
        Assert.True(AttendanceService.IsPostMidnightClockOutOfDayShift(Tue.ToDateTime(new TimeOnly(5, 30)), yesterdayIn, DayShift(), null));
        // 今天排了班：判断口径改成看"今天这个班次自己的上班时间提前 6 小时"，08:30 上班时 05:30 不算"太早"
        // （不满足"太早"就不会被当成昨天延续过来的下班卡——这是本来就有的、跟这次收紧无关的既有规则）
        Assert.False(AttendanceService.IsPostMidnightClockOutOfDayShift(Tue.ToDateTime(new TimeOnly(5, 30)), yesterdayIn, null, DayShift()));
        Assert.True(AttendanceService.IsPostMidnightClockOutOfDayShift(Tue.ToDateTime(new TimeOnly(1, 30)), yesterdayIn, null, DayShift()));
    }

    [Fact]
    public void 补卡下班卡_没有班次可参照时_填反的时间不再被强行顺延到第二天()
    {
        // 没排班的人把 18:00 手滑填成 08:00（比上班还早）：以前不管三七二十一顺延到第二天，
        // 会算出一个 22 小时的班；现在原样保留在当天，交给"下班时间早于上班时间"的时间异常规则去提醒人工核实
        var result = AttendanceService.ResolvePunchReplenishmentClockOut(Mon, new TimeOnly(8, 0), Mon.ToDateTime(new TimeOnly(8, 30)), null);
        Assert.Equal(Mon.ToDateTime(new TimeOnly(8, 0)), result);
    }

    [Fact]
    public void 补卡下班卡_没有班次但填的是凌晨时间_仍然顺延到第二天()
    {
        // 没有班次信息，但填的时间在凌晨（<06:00），看起来确实像是"第二天早上几点下班"，继续允许顺延
        var result = AttendanceService.ResolvePunchReplenishmentClockOut(Mon, new TimeOnly(5, 0), Mon.ToDateTime(new TimeOnly(8, 30)), null);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(5, 0)), result);
    }

    [Fact]
    public async Task 设备同步_完全没排班的人_昨天10点上班忘打下班卡_今天凌晨的卡不会被并成18小时的班()
    {
        int uid;
        using (var db = CreateContext())
        {
            var user = new User { EmployeeNo = "NS1", RealName = "没排班员工", PasswordHash = "x", IsActive = true, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            var dev = new ZKDevice { SN = "SNNS", IsActive = true };
            db.ZKDevices.Add(dev);
            db.SaveChanges();
            db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
            // 昨天 10:00 上班，没打下班卡；今天、昨天都没有任何排班（ShiftAssignment 一条都不建）
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = user.Id, WorkDate = Mon, ClockInTime = Mon.ToDateTime(new TimeOnly(10, 0)), AttendanceStatus = AttendanceStatus.Normal });
            db.SaveChanges();
            uid = user.Id;
        }
        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await svc.ProcessAttLogAsync("SNNS", [new ZKAttLogRow("NS1", Tue.ToDateTime(new TimeOnly(5, 30)), 0, 15)]);
        }
        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Null(mon.ClockOutTime);                              // 昨天那条记录没被这次凌晨的卡接走
        var tue = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);   // 今天单独有自己的记录
        Assert.Equal(Tue.ToDateTime(new TimeOnly(5, 30)), tue.ClockInTime);
    }

    [Fact]
    public void 休息日有打卡_不算出勤天数_请假当天不受影响()
    {
        var work = new AttendanceRecord { ClockInTime = Sat.ToDateTime(new TimeOnly(9, 0)), AttendanceStatus = AttendanceStatus.Normal };
        Assert.Equal(1m, AttendanceService.ResolveAttendanceDayCredit(work, 8, isRestDay: false));
        Assert.Equal(0m, AttendanceService.ResolveAttendanceDayCredit(work, 8, isRestDay: true));
        var leave = new AttendanceRecord { ClockInTime = Sat.ToDateTime(new TimeOnly(9, 0)), AttendanceStatus = AttendanceStatus.OnLeave, LeaveHours = 4 };
        Assert.Equal(0.5m, AttendanceService.ResolveAttendanceDayCredit(leave, 8, isRestDay: true));
    }

    [Fact]
    public async Task 月度汇总和模板汇总表_休息日来打卡但没批加班_出勤天数不算_夜班天数也不算()
    {
        var (uid, _) = SeedWeekWorld();   // 周五到周一都排了白班，周六周日是休息日
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Fri, ClockInTime = Fri.ToDateTime(new TimeOnly(8, 25)), ClockOutTime = Fri.ToDateTime(new TimeOnly(17, 35)), ActualWorkHours = 8, AttendanceStatus = AttendanceStatus.Normal });
            // 周六晚上 19:00 来打卡（休息日、没批加班），工时 0
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Sat, ClockInTime = Sat.ToDateTime(new TimeOnly(19, 0)), ClockOutTime = Sat.ToDateTime(new TimeOnly(21, 0)), ActualWorkHours = 0, AttendanceStatus = AttendanceStatus.Normal });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var report = await svc.GenerateTemplateReportAsync(Fri, Mon, null);
        var row = report.Rows.Single(r => r.EmployeeNo == "L1");
        Assert.Equal(1m, row.ActualWorkdays);        // 只有周五；周六不算出勤
        Assert.Equal(0, row.NightShiftDays);         // 排了白班，周六晚上来打卡不算夜班
        await svc.GenerateMonthlySummaryAsync(Fri.Year, Fri.Month, new[] { uid });
        var sum = await db2.MonthlyAttendanceSummaries.AsNoTracking().SingleAsync(x => x.UserId == uid);
        Assert.Equal(1m, sum.ActualWorkdays);
    }

    [Fact]
    public async Task 没排班的日子_夜班天数仍按打卡时间兜底()
    {
        var (uid, _) = SeedWeekWorld();
        using (var db = CreateContext())
        {
            // 周一没有排班（把周一的排班删掉），晚上 19:00 上班 → 兜底算夜班
            db.ShiftAssignments.RemoveRange(db.ShiftAssignments.Where(a => a.UserId == uid && a.WorkDate == Mon));
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Mon, ClockInTime = Mon.ToDateTime(new TimeOnly(19, 0)), AttendanceStatus = AttendanceStatus.Normal });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var report = await new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance).GenerateTemplateReportAsync(Fri, Mon, null);
        Assert.Equal(1, report.Rows.Single(r => r.EmployeeNo == "L1").NightShiftDays);
    }

    [Fact]
    public async Task 模板汇总表_全公司放假加本考勤组补班同一天_按统一优先级算工作日()
    {
        var (uid, gid) = SeedWeekWorld();
        using (var db = CreateContext())
        {
            // 周一：全公司放假 + 本考勤组补班。统一规则：考勤组自己的规则优先 → 补班（工作日）
            db.Holidays.Add(new Holiday { HolidayName = "全公司放假", HolidayDate = Mon, HolidayType = HolidayType.LegalHoliday });
            db.Holidays.Add(new Holiday { HolidayName = "本组补班", HolidayDate = Mon, HolidayType = HolidayType.CompensatoryWorkDay, AttendanceGroupId = gid });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var report = await new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance).GenerateTemplateReportAsync(Mon, Mon, null);
        var row = report.Rows.Single(r => r.EmployeeNo == "L1");
        Assert.False(row.DailyIsRest[0]);            // 补班日：不是休息
        Assert.Equal(1, row.ExpectedWorkdays);       // 跟"应出勤"一致
    }

    [Fact]
    public async Task 管理员手动补卡_打卡时间不在考勤日当天或第二天_直接拒绝_下班早于上班也拒绝()
    {
        var (uid, _) = SeedWeekWorld();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var wrongMonth = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AdminAdjustPunchAsync(uid, Mon, new DateTime(2026, 8, 7, 8, 30, 0), null, null, "管理员"));
        Assert.Contains("请检查日期", wrongMonth.Message);
        var reversed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AdminAdjustPunchAsync(uid, Mon, Mon.ToDateTime(new TimeOnly(17, 0)), Mon.ToDateTime(new TimeOnly(8, 0)), null, "管理员"));
        Assert.Contains("下班时间必须晚于上班时间", reversed.Message);
        // 夜班下班在第二天：允许
        await svc.AdminAdjustPunchAsync(uid, Mon, Mon.ToDateTime(new TimeOnly(20, 0)), Tue.ToDateTime(new TimeOnly(8, 0)), null, "管理员");
    }

    // ── ⑯ 2026-09-29 用户反馈：夜班一次卡都没打，凌晨才想起来打上班卡，被"打得太早"误拦 ──────

    [Theory]
    [InlineData(0, 29, false)]    // 昨晚 20:00 上班，现在凌晨 00:29：还在昨晚班次结束（08:00）之前，应该放行
    [InlineData(7, 59, false)]    // 还差 1 分钟到昨晚班次的下班时间：仍放行
    [InlineData(8, 1, true)]      // 已经过了昨晚班次的下班时间：不再算"昨晚很晚的上班卡"，落回原来"打得太早"的判断
    public async Task 夜班一次卡都没打_凌晨才打卡_只要没超过昨晚班次下班时间就不算太早(int h, int m, bool stillRejected)
    {
        var (uid, _) = SeedNightWorld();
        using (var db = CreateContext())
        {
            // 把 SeedNightWorld 帮我们建好的"周一 20:00 已打上班卡"抹掉，改成"周一一次卡都没打"（更贴近真实反馈的场景）
            var mon = await db.AttendanceRecords.SingleAsync(r => r.WorkDate == Mon);
            db.AttendanceRecords.Remove(mon);
            await db.SaveChangesAsync();
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var msg = await svc.GetClockInRejectionAsync(uid, Tue.ToDateTime(new TimeOnly(h, m)));
        if (stillRejected)
        {
            Assert.NotNull(msg);
            Assert.Contains("最早", msg);
        }
        else
        {
            Assert.Null(msg);
        }
    }

    [Fact]
    public async Task 夜班一次卡都没打_凌晨打卡成功后_记到昨天那班_算很晚的迟到_不会凭空多出今天的记录()
    {
        // GetClockInRejectionAsync 只能验证"拦不拦"，实际落库走的是 PunchAsync（内部用 DateTime.Now，
        // 没法像别的测试那样注入固定的 Mon/Tue），所以这条改用真实的"今天/昨天"，并把班次的下班时间
        // 设得很晚（23:59），保证不管测试什么时候跑，"现在"都落在"昨晚班次结束之前"这个窗口内。
        var today     = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "夜班一次没打组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            var shift = new ShiftSchedule
            {
                ShiftName = "夜班", AttendanceGroupId = group.Id, WorkStartTime = new TimeOnly(20, 0), WorkEndTime = new TimeOnly(23, 59),
                IsCrossDay = true, LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 3, RestDaysOfWeek = ""
            };
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "N9", RealName = "夜班没打卡员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            foreach (var d in new[] { yesterday, today })
                db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
            db.SaveChanges();
            uid = user.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var result = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockIn }, skipLocationCheck: true);
            Assert.True(result.Success, result.Message);
        }
        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == today));   // 没有凭空多出"今天"的记录
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == yesterday);
        Assert.NotNull(rec.ClockInTime);
        Assert.Equal(AttendanceStatus.Late, rec.AttendanceStatus);
        Assert.True(rec.LateMinutes > 0);
    }

    [Fact]
    public async Task 设备同步_夜班一次卡都没打_凌晨的脸识别记成昨晚很晚的上班卡()
    {
        var (uid, _) = SeedNightWorld();
        using (var db = CreateContext())
        {
            var mon = await db.AttendanceRecords.SingleAsync(r => r.WorkDate == Mon);
            db.AttendanceRecords.Remove(mon);   // 周一一次卡都没打
            await db.SaveChangesAsync();
        }
        await SyncAsync(Tue.ToDateTime(new TimeOnly(0, 29)));

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
        var mon2 = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(0, 29)), mon2.ClockInTime);
        Assert.Equal(AttendanceStatus.Late, mon2.AttendanceStatus);
    }
}
