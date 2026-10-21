using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>夜班过了零点之后接着干的加班，要记到前一个考勤日（夜班的延续），不能记到"开始时间所在的日历日"。</summary>
public class OvertimeNightAttributionTests : SqliteTestBase
{
    private async Task ApplyOvertimeAsync(int uid, DateTime start, DateTime end, string no)
    {
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = no, ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            OvertimeStartTime = start, OvertimeEndTime = end, OvertimeDurationHours = (decimal)(end - start).TotalHours, Reason = "t"
        });
        using var db = CreateContext();
        await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);
    }

    [Fact]
    public async Task 夜班下班后接着干的早晨加班_记到前一个考勤日_后一天不出现空记录()
    {
        var (uid, _) = SeedNightWorld();   // 周一、周二、周三都排夜班 20:00–次日 08:00
        await ApplyOvertimeAsync(uid, Tue.ToDateTime(new TimeOnly(8, 30)), Tue.ToDateTime(new TimeOnly(11, 0)), "JB-N-1");

        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(2.5m, mon.OvertimeHours);
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));   // 以前会在周二凭空多出一条
    }

    [Fact]
    public async Task 夜班员工当天晚上开始的加班_仍记当天()
    {
        var (uid, _) = SeedNightWorld();
        // 周四没排班：按他最近排的夜班（20:00 起）兜底，晚上 18:00–19:30 的加班在夜班开始之前、不重叠，记周四当天
        // （20:30 之后的加班会和兜底的夜班重叠，只记班次之外的部分，见 OvertimeShiftReconcileTests）
        var thu = Tue.AddDays(2);
        await ApplyOvertimeAsync(uid, thu.ToDateTime(new TimeOnly(18, 0)), thu.ToDateTime(new TimeOnly(19, 30)), "JB-N-2");

        using var check = CreateContext();
        Assert.Equal(1.5m, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == thu)).OvertimeHours);
    }

    [Fact]
    public async Task 夜班员工下午开始的加班_不当作夜班延续_记当天()
    {
        var (uid, _) = SeedNightWorld();
        await ApplyOvertimeAsync(uid, Tue.ToDateTime(new TimeOnly(15, 0)), Tue.ToDateTime(new TimeOnly(17, 0)), "JB-N-3");

        using var check = CreateContext();
        Assert.Equal(2m, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue)).OvertimeHours);
    }

    [Fact]
    public async Task 白班员工的早晨加班_前一天不是夜班_仍记当天()
    {
        var uid = SeedDayDeviceWorld();
        var day = Tue;
        await ApplyOvertimeAsync(uid, day.ToDateTime(new TimeOnly(6, 30)), day.ToDateTime(new TimeOnly(8, 0)), "JB-D-1");

        using var check = CreateContext();
        Assert.Equal(1.5m, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == day)).OvertimeHours);
    }
}
