using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 审批回写的幂等保护（2026-11-06 审计 R3）：
/// 同一张加班单/请假单如果被回写两次（补跑工具、重试等），加班时长、请假小时数不能加两遍。
/// </summary>
public class ApprovalWritebackIdempotencyTests : SqliteTestBase
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private int SeedEmployee()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "幂等测试组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var user = U("IDEM1", "幂等测试");
        user.AttendanceGroupId = group.Id;
        db.Users.Add(user);
        db.SaveChanges();

        var shift = DayShift();                  // 08:30–17:30，没有固定休息日
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        db.SaveChanges();

        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = Today, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static string NewNo(string prefix) => prefix + "-IDEM-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<int> AddApprovedOvertimeAsync(int uid)
    {
        return await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = NewNo("JB"), ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            OvertimeStartTime = Today.ToDateTime(new TimeOnly(18, 0)),
            OvertimeEndTime   = Today.ToDateTime(new TimeOnly(20, 0)),
            OvertimeDurationHours = 2, Reason = "t"
        });
    }

    private async Task<int> AddApprovedLeaveAsync(int uid)
    {
        return await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = NewNo("QJ"), ApplicantUserId = uid, ApprovalType = ApprovalType.Leave,
            LeaveType = LeaveType.PersonalLeave,
            LeaveStartTime = Today.ToDateTime(new TimeOnly(13, 0)),
            LeaveEndTime   = Today.ToDateTime(new TimeOnly(17, 30)),
            LeaveDurationHours = 4.5m, Reason = "t"
        });
    }

    [Fact]
    public async Task 同一张加班单回写两次_加班时长只加一次()
    {
        var uid = SeedEmployee();
        var id  = await AddApprovedOvertimeAsync(uid);

        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);

        await svc.UpdateAttendanceAfterApprovalAsync(id);   // 第一次：正常加 2 小时
        var first = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Today);
        var afterFirst = first.OvertimeHours;
        Assert.Equal(2m, afterFirst);
        // 幂等标记：回写完成后单子上会记下回写时间（备注被截断也不影响判断）
        var approvalAfterFirst = await db.ApprovalRequests.SingleAsync(a => a.Id == id);
        Assert.NotNull(approvalAfterFirst.WritebackAppliedAt);

        await svc.UpdateAttendanceAfterApprovalAsync(id);   // 第二次：必须跳过
        db.ChangeTracker.Clear();
        var again = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Today);
        Assert.Equal(afterFirst, again.OvertimeHours);
    }

    [Fact]
    public async Task 同一张请假单回写两次_请假小时数只加一次()
    {
        var uid = SeedEmployee();
        var id  = await AddApprovedLeaveAsync(uid);

        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);

        await svc.UpdateAttendanceAfterApprovalAsync(id);   // 第一次：正常记 4.5 小时请假
        var first = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Today);
        var afterFirst = first.LeaveHours;
        Assert.Equal(4.5m, afterFirst);

        await svc.UpdateAttendanceAfterApprovalAsync(id);   // 第二次：必须跳过
        db.ChangeTracker.Clear();
        var again = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Today);
        Assert.Equal(afterFirst, again.LeaveHours);
    }
}
