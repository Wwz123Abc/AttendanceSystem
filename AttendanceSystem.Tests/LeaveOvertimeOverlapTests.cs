using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 加班与请假时段重叠校验（2026-11-05 用户要求）：
/// 同一段时间既批了假又报加班会按"请假封顶 + 加班时长"重复算钱，两个方向、提交和回写都要拦。
/// 判断标准是"那天实际计入请假的时段"——多天假单不会把晚上/周末的加班也一起拦掉。
/// </summary>
public class LeaveOvertimeOverlapTests : SqliteTestBase
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static DateTime At(int h, int m) => Today.ToDateTime(new TimeOnly(h, m));

    private static string No() => "OVL-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>造一个"今天排了白班 08:30–17:30"的员工（有直属上级，提交申请才走得通）。</summary>
    private int SeedEmployee(string restDays = "")
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var boss = new User { EmployeeNo = "B1", RealName = "上级", PasswordHash = "x", IsActive = true, Role = UserRole.Supervisor };
        db.Users.Add(boss);
        db.SaveChanges();
        var user = new User
        {
            EmployeeNo = "E1", RealName = "员工", PasswordHash = "x", IsActive = true,
            AttendanceGroupId = group.Id, SupervisorUserId = boss.Id, HireDate = new DateOnly(2026, 1, 1)
        };
        db.Users.Add(user);
        db.SaveChanges();
        var shift = DayShift();
        shift.AttendanceGroupId = group.Id;
        shift.RestDaysOfWeek = restDays;
        db.ShiftSchedules.Add(shift);
        db.SaveChanges();
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = Today, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return user.Id;
    }

    private async Task AddApprovedLeaveAsync(int uid, LeaveType type, DateTime start, DateTime end)
    {
        await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = No(), ApplicantUserId = uid, ApprovalType = ApprovalType.Leave,
            ApprovalStatus = ApprovalStatus.Approved, LeaveType = type,
            LeaveStartTime = start, LeaveEndTime = end, LeaveDurationHours = 8, Reason = "t"
        });
    }

    private async Task AddApprovedOvertimeAsync(int uid, DateTime start, DateTime end)
    {
        await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = No(), ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            ApprovalStatus = ApprovalStatus.Approved,
            OvertimeStartTime = start, OvertimeEndTime = end, OvertimeDurationHours = 2, Reason = "t"
        });
    }

    // ── 规则本身（两个方向 + 逐天判断）────────────────────────────────

    [Fact]
    public async Task 婚假是自然日_同一天晚上的加班也算重叠()
    {
        var uid = SeedEmployee();
        await AddApprovedLeaveAsync(uid, LeaveType.MarriageLeave, Today.ToDateTime(TimeOnly.MinValue), Today.AddDays(1).ToDateTime(TimeOnly.MinValue));

        using var db = CreateContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => LeaveOvertimeOverlapGuard.EnsureNoOverlapAsync(
            db, uid, At(18, 30), At(19, 30), ApprovalType.Overtime));
        Assert.Contains("请假", ex.Message);
    }

    [Fact]
    public async Task 事假只占班次上班时段_下班后的加班允许()
    {
        var uid = SeedEmployee();
        await AddApprovedLeaveAsync(uid, LeaveType.PersonalLeave, At(8, 30), At(17, 30));

        using var db = CreateContext();
        // 请假只占 08:30–17:30，晚上 18:30–19:30 的加班不重叠，允许
        await LeaveOvertimeOverlapGuard.EnsureNoOverlapAsync(db, uid, At(18, 30), At(19, 30), ApprovalType.Overtime);
        // 但 15:00–16:00 落在请假时段里，要拦
        await Assert.ThrowsAsync<BusinessException>(() => LeaveOvertimeOverlapGuard.EnsureNoOverlapAsync(
            db, uid, At(15, 0), At(16, 0), ApprovalType.Overtime));
    }

    [Fact]
    public async Task 多天事假期间_休息日的加班不算重叠()
    {
        var uid = SeedEmployee(restDays: "0,6");   // 周六日休息
        // 10-24 是周六：一张覆盖 10-20~11-20 的长事假，不应该影响周六的加班
        var saturday = new DateOnly(2026, 10, 24);
        await AddApprovedLeaveAsync(uid, LeaveType.PersonalLeave,
            new DateOnly(2026, 10, 20).ToDateTime(new TimeOnly(8, 30)),
            new DateOnly(2026, 11, 20).ToDateTime(new TimeOnly(17, 30)));

        // 重叠校验按"那天排的班次"判断是不是休息日，所以周六也要有一条排班记录（同班次，配置里周六日休息）；
        // 没排班的那天按自然日算（跟 ComputeLeaveHoursForDay 的口径一致），那种情况本来就算请假
        using (var seed = CreateContext())
        {
            var shiftId = seed.ShiftAssignments.Where(a => a.UserId == uid).Select(a => a.ShiftScheduleId).First();
            seed.ShiftAssignments.Add(new ShiftAssignment { UserId = uid, WorkDate = saturday, ShiftScheduleId = shiftId });
            await seed.SaveChangesAsync();
        }

        using var db = CreateContext();
        await LeaveOvertimeOverlapGuard.EnsureNoOverlapAsync(
            db, uid, saturday.ToDateTime(new TimeOnly(8, 30)), saturday.ToDateTime(new TimeOnly(21, 0)), ApprovalType.Overtime);
    }

    [Fact]
    public async Task 已批准加班期间_不能请重叠的假()
    {
        var uid = SeedEmployee();
        await AddApprovedOvertimeAsync(uid, At(13, 0), At(14, 0));

        using var db = CreateContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => LeaveOvertimeOverlapGuard.EnsureNoOverlapAsync(
            db, uid, At(13, 0), At(14, 0), ApprovalType.Leave, submittingLeaveType: LeaveType.PersonalLeave));
        Assert.Contains("加班", ex.Message);
    }

    // ── 提交申请时的拦截（走 ApprovalService，验证校验已接上）──────────

    [Fact]
    public async Task 提交加班时_被婚假拦下()
    {
        var uid = SeedEmployee();
        await AddApprovedLeaveAsync(uid, LeaveType.MarriageLeave, Today.ToDateTime(TimeOnly.MinValue), Today.AddDays(1).ToDateTime(TimeOnly.MinValue));

        var ex = await TrySubmitOvertimeAsync(uid, new TimeOnly(18, 30), new TimeOnly(19, 30));
        Assert.NotNull(ex);
        Assert.Contains("请假", ex!.Message);
    }

    [Fact]
    public async Task 提交加班时_事假不拦下班后的加班()
    {
        var uid = SeedEmployee();
        await AddApprovedLeaveAsync(uid, LeaveType.PersonalLeave, At(8, 30), At(17, 30));

        var ex = await TrySubmitOvertimeAsync(uid, new TimeOnly(18, 30), new TimeOnly(19, 30));
        Assert.Null(ex);
    }

    [Fact]
    public async Task 提交请假时_被已批准的加班拦下()
    {
        var uid = SeedEmployee();
        await AddApprovedOvertimeAsync(uid, At(13, 0), At(14, 0));

        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.PersonalLeave, Reason = "t",
            LeaveStartTime = At(13, 0), LeaveEndTime = At(14, 0)
        }));
        Assert.Contains("加班", ex.Message);
    }
}
