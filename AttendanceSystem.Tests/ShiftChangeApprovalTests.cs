using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>转班申请（2026-10-26）：员工临时把某几天的班次改成另一个班次，审批通过后自动改排班。</summary>
public class ShiftChangeApprovalTests : SqliteTestBase
{
    private static readonly DateTime Now = Tue.ToDateTime(new TimeOnly(9, 0));   // "今天"是周二

    /// <summary>员工在组里，白班排在 周五到周一（SeedWeekWorld）；再加一个启用的夜班、一个停用的班次、别的考勤组的一个班次，以及兜底管理员。</summary>
    private (int uid, int dayId, int nightId, int disabledId, int otherGroupShiftId) Seed()
    {
        var (uid, groupId) = SeedWeekWorld();
        using var db = CreateContext();
        var day = db.ShiftSchedules.Single(s => s.AttendanceGroupId == groupId);
        var night = NightShift(); night.AttendanceGroupId = groupId;
        var disabled = DayShift(); disabled.AttendanceGroupId = groupId; disabled.ShiftName = "停用班"; disabled.IsActive = false;
        var otherGroup = new AttendanceGroup { GroupName = "别的组" };
        db.AttendanceGroups.Add(otherGroup);
        db.ShiftSchedules.AddRange(night, disabled);
        db.SaveChanges();
        var other = DayShift(); other.AttendanceGroupId = otherGroup.Id; other.ShiftName = "别组白班";
        var admin = U("ADM-SC", "兜底管理员"); admin.Role = UserRole.Admin;
        db.ShiftSchedules.Add(other);
        db.Users.Add(admin);
        db.SaveChanges();
        return (uid, day.Id, night.Id, disabled.Id, other.Id);
    }

    private async Task<string?> SubmitAsync(int uid, DateOnly start, DateOnly end, int? targetShiftId, DateTime? now = null)
    {
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions, new FixedClock(now ?? Now));
        try
        {
            await svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
            {
                ApprovalType = ApprovalType.ShiftChange, Reason = "临时换班",
                ShiftChangeStartDate = start, ShiftChangeEndDate = end, ShiftChangeTargetShiftId = targetShiftId
            });
            return null;
        }
        catch (BusinessException ex) { return ex.Message; }
    }

    [Fact]
    public async Task 提交成功_单号ZB开头_记下目标班次名称()
    {
        var (uid, _, nightId, _, _) = Seed();
        Assert.Null(await SubmitAsync(uid, Tue.AddDays(1), Tue.AddDays(3), nightId));
        using var db = CreateContext();
        var req = await db.ApprovalRequests.SingleAsync();
        Assert.Equal(ApprovalType.ShiftChange, req.ApprovalType);
        Assert.StartsWith("ZB", req.RequestNo);
        Assert.Equal("夜班", req.ShiftChangeTargetShiftName);
        Assert.Equal(Tue.AddDays(1), req.ShiftChangeStartDate);
        Assert.Equal(Tue.AddDays(3), req.ShiftChangeEndDate);
    }

    [Fact]
    public async Task 日期限制_最多补报最近3天_最多连续7天_结束不能早于开始()
    {
        var (uid, _, nightId, _, _) = Seed();
        Assert.Null(await SubmitAsync(uid, Tue.AddDays(-3), Tue.AddDays(-3), nightId));                         // 刚好 3 天前：可以
        Assert.Contains("最近 3 天", await SubmitAsync(uid, Tue.AddDays(-4), Tue.AddDays(-4), nightId));        // 4 天前：不行
        Assert.Contains("最多连续 7 天", await SubmitAsync(uid, Tue.AddDays(10), Tue.AddDays(17), nightId));    // 8 天：不行
        Assert.Null(await SubmitAsync(uid, Tue.AddDays(10), Tue.AddDays(16), nightId));                          // 7 天：可以
        Assert.Contains("不能早于开始日期", await SubmitAsync(uid, Tue.AddDays(5), Tue.AddDays(4), nightId));
    }

    [Fact]
    public async Task 目标班次必须是本组启用中的班次()
    {
        var (uid, _, _, disabledId, otherGroupShiftId) = Seed();
        Assert.Contains("不存在，或不属于你所在的考勤组", await SubmitAsync(uid, Tue.AddDays(1), Tue.AddDays(1), disabledId));
        Assert.Contains("不存在，或不属于你所在的考勤组", await SubmitAsync(uid, Tue.AddDays(1), Tue.AddDays(1), otherGroupShiftId));
        Assert.Contains("不存在，或不属于你所在的考勤组", await SubmitAsync(uid, Tue.AddDays(1), Tue.AddDays(1), 99999));
        Assert.Contains("请选择要转到的班次", await SubmitAsync(uid, Tue.AddDays(1), Tue.AddDays(1), null));
    }

    [Fact]
    public async Task 这几天排的已经都是目标班次_不需要转班()
    {
        var (uid, dayId, _, _, _) = Seed();
        // 周五到周一都排的是白班
        Assert.Contains("不需要转班", await SubmitAsync(uid, Fri, Mon, dayId, now: Fri.ToDateTime(new TimeOnly(9, 0))));
    }

    [Fact]
    public async Task 日期重叠的有效转班申请不能重复提交()
    {
        var (uid, _, nightId, _, _) = Seed();
        Assert.Null(await SubmitAsync(uid, Tue.AddDays(1), Tue.AddDays(3), nightId));
        Assert.Contains("已经有转班申请", await SubmitAsync(uid, Tue.AddDays(3), Tue.AddDays(4), nightId));   // 周四重叠
        Assert.Null(await SubmitAsync(uid, Tue.AddDays(4), Tue.AddDays(5), nightId));                          // 不重叠
    }

    [Fact]
    public async Task 审批通过后回写_已有排班改成目标班次_没排班的日子新建_排的是目标班次()
    {
        var (uid, _, nightId, _, _) = Seed();
        // 周日到下周二：周日、周一已排白班，周二（9/8）之后没排班
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "ZB-T-1", ApplicantUserId = uid, ApprovalType = ApprovalType.ShiftChange, Reason = "t",
            ShiftChangeStartDate = Sun, ShiftChangeEndDate = Tue, ShiftChangeTargetShiftId = nightId, ShiftChangeTargetShiftName = "夜班"
        });
        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);

        using var check = CreateContext();
        var rows = await check.ShiftAssignments.Where(a => a.UserId == uid && a.WorkDate >= Sun && a.WorkDate <= Tue).OrderBy(a => a.WorkDate).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, a => Assert.Equal(nightId, a.ShiftScheduleId));
        // 周六不在转班范围内：仍然是白班
        Assert.NotEqual(nightId, (await check.ShiftAssignments.SingleAsync(a => a.UserId == uid && a.WorkDate == Sat)).ShiftScheduleId);
    }

    [Fact]
    public async Task 入职日期之前的日子不排班()
    {
        var (uid, _, nightId, _, _) = Seed();
        using (var db = CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == uid)).HireDate = Mon;   // 周一才入职
            await db.SaveChangesAsync();
        }
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "ZB-T-2", ApplicantUserId = uid, ApprovalType = ApprovalType.ShiftChange, Reason = "t",
            ShiftChangeStartDate = Sun, ShiftChangeEndDate = Mon, ShiftChangeTargetShiftId = nightId, ShiftChangeTargetShiftName = "夜班"
        });
        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);

        using var check = CreateContext();
        Assert.NotEqual(nightId, (await check.ShiftAssignments.SingleAsync(a => a.UserId == uid && a.WorkDate == Sun)).ShiftScheduleId);   // 周日在入职前：不动
        Assert.Equal(nightId, (await check.ShiftAssignments.SingleAsync(a => a.UserId == uid && a.WorkDate == Mon)).ShiftScheduleId);
    }
}
