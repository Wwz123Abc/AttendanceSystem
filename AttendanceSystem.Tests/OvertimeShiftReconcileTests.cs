using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 加班不能和班次重叠，也不能依赖"排班有没有及时排上"（2026-10-21：朱志方等人 10/1–10/5 的整天加班单，提交时没排班，
/// 重叠检查被跳过，正班 8 小时和整天加班重复计算）。没排班时用考勤组最常用的日班兜底；排班补上后重新对账。
/// </summary>
public class OvertimeShiftReconcileTests : SqliteTestBase
{
    private static DateTime At(DateOnly d, int h, int m = 0) => d.ToDateTime(new TimeOnly(h, m));

    [Fact]
    public void 加班时长_工作日只记班次之外的部分_休息日整段记()
    {
        var day = DayShift();   // 08:30–17:30，周六周日休息
        // 周二（工作日）08:30–20:30：只记 17:30 之后，扣晚饭 17:30–18:00 的 0.5 小时 → 2.5
        Assert.Equal(2.5m, AttendanceService.ComputeOvertimeHoursOutsideShift(At(Tue, 8, 30), At(Tue, 20, 30), [(Tue, day)]));
        // 下班后的加班本来就不重叠：18:30–21:00 = 2.5
        Assert.Equal(2.5m, AttendanceService.ComputeOvertimeHoursOutsideShift(At(Tue, 18, 30), At(Tue, 21, 0), [(Tue, day)]));
        // 休息日（周六）整段都是加班：08:30–20:30 = 12 小时，扣午饭 1 小时、晚饭 0.5 小时 = 10.5
        Assert.Equal(10.5m, AttendanceService.ComputeOvertimeHoursOutsideShift(At(Sat, 8, 30), At(Sat, 20, 30), [(Sat, day)]));
    }

    [Fact]
    public void 加班时长_夜班下班后接着干的早晨加班_去掉昨晚夜班的上班时间()
    {
        var night = NightShift();   // 20:00–次日 08:00
        // 周二 06:00–10:00 的加班，前一天（周一）是夜班：06:00–08:00 在夜班里，只记 08:00–10:00 = 2
        Assert.Equal(2m, AttendanceService.ComputeOvertimeHoursOutsideShift(At(Tue, 6, 0), At(Tue, 10, 0), [(Mon, night)]));
    }

    private (int uid, int gid) SeedUnscheduledUser()
    {
        var (_, gid) = SeedWeekWorld();   // 组里有白班 08:30–17:30，L1 排了周五到周一
        using var db = CreateContext();
        var u = new User { EmployeeNo = "L2", RealName = "没排班的人", PasswordHash = "x", IsActive = true, AttendanceGroupId = gid, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(u); db.SaveChanges();
        return (u.Id, gid);
    }

    [Fact]
    public async Task 没排班的人提交整天加班_用考勤组的日班兜底_照样被拦()
    {
        var (uid, _) = SeedUnscheduledUser();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions, new FixedClock(At(Tue, 10)));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Overtime, Reason = "t", OvertimeStartTime = At(Tue, 8, 30), OvertimeEndTime = At(Tue, 20, 30)
        }));
        Assert.Contains("不能和上班时间重叠", ex.Message);
    }

    [Fact]
    public async Task 先提交加班_后补排班_对账后加班只记班次之外的部分()
    {
        var (uid, gid) = SeedUnscheduledUser();
        int shiftId;
        using (var db = CreateContext())
        {
            shiftId = db.ShiftSchedules.Single(s => s.AttendanceGroupId == gid).Id;
            // 事故场景：当时没排班，整天加班单被批了，记录上正班 8 + 加班 10.5
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = Tue, ClockInTime = At(Tue, 8, 20), ClockOutTime = At(Tue, 21, 0),
                ActualWorkHours = 8m, OvertimeHours = 10.5m, AttendanceStatus = AttendanceStatus.Normal
            });
            await db.SaveChangesAsync();
        }
        var formId = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "JB-T-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            OvertimeStartTime = At(Tue, 8, 30), OvertimeEndTime = At(Tue, 20, 30), OvertimeDurationHours = 10.5m, Reason = "t"
        });

        using (var db = CreateContext())
        {
            // 先只看（不写库）：应该报告这一天从 10.5 降到 2.5
            var dry = await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance)
                .ReconcileOvertimeAsync([uid], Tue, Tue, dryRun: true);
            var row = Assert.Single(dry);
            Assert.Equal(10.5m, row.OldOvertime);
            Assert.Equal(2.5m, row.NewOvertime);
            Assert.Equal(10.5m, (await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue)).OvertimeHours);   // dryRun 没写库
        }
        using (var db = CreateContext())
        {
            // 真正对账（排班补上之后系统自动调用的就是这个）
            var applied = await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance)
                .ReconcileOvertimeAfterScheduleAsync([uid], Tue, Tue);
            Assert.Equal(1, applied);
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(2.5m, rec.OvertimeHours);
        Assert.Equal(8m, rec.ActualWorkHours);   // 正班不动
        Assert.Equal(2.5m, (await check.ApprovalRequests.SingleAsync(a => a.Id == formId)).OvertimeDurationHours);
        Assert.Contains("已按班次对账", rec.ApprovalNote);
        _ = shiftId;
    }

    [Fact]
    public async Task 对账_已经正确的记录不动_重复跑结果不变()
    {
        var (uid, _) = SeedUnscheduledUser();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = Tue, ClockInTime = At(Tue, 8, 20), ClockOutTime = At(Tue, 21, 0),
                ActualWorkHours = 8m, OvertimeHours = 2.5m, AttendanceStatus = AttendanceStatus.Normal
            });
            await db.SaveChangesAsync();
        }
        await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "JB-T-2", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            OvertimeStartTime = At(Tue, 18, 30), OvertimeEndTime = At(Tue, 21, 0), OvertimeDurationHours = 2.5m, Reason = "t"
        });
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        Assert.Empty(await svc.ReconcileOvertimeAsync([uid], Tue, Tue, dryRun: false));
        Assert.Empty(await svc.ReconcileOvertimeAsync([uid], Tue, Tue, dryRun: false));
    }

    [Fact]
    public void 兜底班次的休息日只按周六周日_排了班的按班次自己的休息日()
    {
        var day = DayShift(); day.RestDaysOfWeek = "2";   // 这个班次配成周二休息
        // 周二整天加班 08:30–20:30：实际排的班 → 周二是休息日，整段 10.5；兜底班次 → 和正班一致只看周六周日，周二是工作日，只记 17:30 后的 2.5
        Assert.Equal(10.5m, AttendanceService.ComputeOvertimeHoursOutsideShift(At(Tue, 8, 30), At(Tue, 20, 30), [(Tue, day)]));
        Assert.Equal(2.5m, AttendanceService.ComputeOvertimeHoursOutsideShift(At(Tue, 8, 30), At(Tue, 20, 30), [(Tue, day)], null, new HashSet<DateOnly> { Tue }));
    }

    [Fact]
    public async Task 夜班员工没排班那天_兜底用他最近排的夜班_不是日班()
    {
        var (uid, _) = SeedNightWorld();   // 周一到周三排夜班
        var thu = Tue.AddDays(2);
        using var db = CreateContext();
        var (shift, isFallback) = await EffectiveShift.ResolveAsync(db, uid, thu, db.Users.Single(u => u.Id == uid).AttendanceGroupId);
        Assert.True(isFallback);
        Assert.True(shift!.IsCrossDay);
    }

    [Fact]
    public async Task 对账_记录上多出来的手工订正不被抹掉_单子少算的部分才扣_单子时长只减不增并留说明()
    {
        var (uid, _) = SeedWeekWorld();   // 周五到周一排白班 08:30–17:30（周末是休息日）
        var fri = Fri;
        // 周五（工作日）：加班单 08:30–20:30 存 10 小时，按班次只算 17:30 之后 = 2.5（少算 7.5）；记录上是 10 + 手工订正 1.5 = 11.5
        int formId;
        using (var db = CreateContext())
        {
            var f = new ApprovalRequest
            {
                RequestNo = "JB-R-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Approved, Reason = "t",
                OvertimeStartTime = At(fri, 8, 30), OvertimeEndTime = At(fri, 20, 30), OvertimeDurationHours = 10
            };
            db.ApprovalRequests.Add(f);
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = fri, AttendanceStatus = AttendanceStatus.Normal, OvertimeHours = 11.5m });
            await db.SaveChangesAsync();
            formId = f.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.ReconcileOvertimeAsync([uid], fri, fri, dryRun: false);
        }
        using var check = CreateContext();
        Assert.Equal(4m, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == fri)).OvertimeHours);   // 11.5 − 7.5：手工订正的 1.5 保留（不是直接赋值成 2.5）
        var form = await check.ApprovalRequests.SingleAsync(f => f.Id == formId);
        Assert.Equal(2.5m, form.OvertimeDurationHours);
        Assert.Contains("按班次对账", form.Reason);
    }
}
