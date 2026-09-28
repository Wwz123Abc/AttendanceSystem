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
}
