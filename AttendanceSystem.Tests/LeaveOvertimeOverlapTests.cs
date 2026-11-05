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
/// </summary>
public class LeaveOvertimeOverlapTests : SqliteTestBase
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static DateTime At(int h, int m) => Today.ToDateTime(new TimeOnly(h, m));

    private async Task AddApprovedLeaveAsync(int uid, DateTime start, DateTime end)
    {
        await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "QJ-OVL-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Leave,
            ApprovalStatus = ApprovalStatus.Approved, LeaveType = LeaveType.PersonalLeave,
            LeaveStartTime = start, LeaveEndTime = end, LeaveDurationHours = 2, Reason = "t"
        });
    }

    private async Task AddApprovedOvertimeAsync(int uid, DateTime start, DateTime end)
    {
        await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "JB-OVL-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            ApprovalStatus = ApprovalStatus.Approved,
            OvertimeStartTime = start, OvertimeEndTime = end, OvertimeDurationHours = 2, Reason = "t"
        });
    }

    // ── 规则本身（两个方向）────────────────────────────────────────

    [Fact]
    public async Task 已批准请假期间_不能报重叠的加班_不重叠可以()
    {
        var uid = SeedOvertimeWorld("");
        await AddApprovedLeaveAsync(uid, At(18, 0), At(20, 0));

        using var db = CreateContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => LeaveOvertimeOverlapGuard.EnsureNoOverlapAsync(
            db, uid, At(18, 30), At(19, 30), ApprovalType.Overtime));
        Assert.Contains("请假", ex.Message);

        // 请假 18:00–20:00，加班 20:00–22:00：首尾相接不算重叠
        await LeaveOvertimeOverlapGuard.EnsureNoOverlapAsync(db, uid, At(20, 0), At(22, 0), ApprovalType.Overtime);
    }

    [Fact]
    public async Task 已批准加班期间_不能请重叠的假()
    {
        var uid = SeedOvertimeWorld("");
        await AddApprovedOvertimeAsync(uid, At(18, 0), At(20, 0));

        using var db = CreateContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => LeaveOvertimeOverlapGuard.EnsureNoOverlapAsync(
            db, uid, At(19, 0), At(21, 0), ApprovalType.Leave));
        Assert.Contains("加班", ex.Message);
    }

    // ── 提交申请时的拦截（走 ApprovalService，验证校验已接上）──────────

    [Fact]
    public async Task 提交加班时_被已批准的请假拦下()
    {
        var uid = SeedOvertimeWorld("");          // 今天 08:30–17:30 白班
        await AddApprovedLeaveAsync(uid, At(18, 0), At(20, 0));

        var ex = await TrySubmitOvertimeAsync(uid, new TimeOnly(18, 30), new TimeOnly(19, 30));
        Assert.NotNull(ex);
        Assert.Contains("请假", ex!.Message);
    }

    [Fact]
    public async Task 提交请假时_被已批准的加班拦下()
    {
        var uid = SeedOvertimeWorld("");
        await AddApprovedOvertimeAsync(uid, At(18, 0), At(20, 0));

        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.PersonalLeave, Reason = "t",
            LeaveStartTime = At(18, 30), LeaveEndTime = At(19, 30)
        }));
        Assert.Contains("加班", ex.Message);
    }
}
