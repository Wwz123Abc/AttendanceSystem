using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// "已经正常排班的员工，正常打卡、补卡、申请加班，都能正确计算工时、输出报表"的端到端回归（2026-10-22）：
/// 每条真实流程（考勤机同步、手机打卡、补卡、加班审批）走完之后，用"数据核对"按现行规则重新算一遍，应该一条疑点都没有；
/// 数据被改乱（工时被改、加班记错、没排班、排错班）时，核对必须能报出来。
/// </summary>
public class AuditFlowTests : SqliteTestBase
{
    private static DateTime At(int h, int m) => Tue.ToDateTime(new TimeOnly(h, m));

    private AttendanceService Svc(AttendanceDbContext db, DateTime? now = null) =>
        new(db, AppOptions, NullLogger<AttendanceService>.Instance, now is { } n ? new FixedClock(n) : null);

    private async Task SyncAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = Svc(db);
        var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await sync.ProcessAttLogAsync("SNM", times.Select(t => new ZKAttLogRow("M1", t, 0, 15)).ToList());
    }

    private async Task<List<AuditIssue>> AuditTueAsync()
    {
        using var db = CreateContext();
        return await Svc(db).AuditAsync(Tue, Tue);
    }

    [Fact]
    public async Task 考勤机正常打卡_核对无疑点()
    {
        SeedDayMidWorld();
        await SyncAsync(At(8, 25), At(12, 20), At(17, 35));
        Assert.Empty(await AuditTueAsync());
    }

    [Fact]
    public async Task 考勤机_午间漏打_按规则只算上午_核对无疑点()
    {
        SeedDayMidWorld();
        await SyncAsync(At(8, 25), At(17, 35));
        Assert.Empty(await AuditTueAsync());
    }

    [Fact]
    public async Task 考勤机_过了午间窗口才到岗_核对无疑点()
    {
        SeedDayMidWorld();
        await SyncAsync(At(13, 40), At(17, 35));
        Assert.Empty(await AuditTueAsync());
    }

    [Fact]
    public async Task 手机打卡全流程_上班_午间_下班_核对无疑点()
    {
        var (uid, _) = SeedDayMidWorld();
        foreach (var (h, m, type) in new[] { (8, 25, PunchType.ClockIn), (12, 20, PunchType.MidCheck), (17, 35, PunchType.ClockOut) })
        {
            using var db = CreateContext();
            var r = await Svc(db, At(h, m)).PunchAsync(uid, new PunchRequestDto { PunchType = type }, skipLocationCheck: true);
            Assert.True(r.Success, r.Message);
        }
        Assert.Empty(await AuditTueAsync());
    }

    [Fact]
    public async Task 管理员补下班卡后_核对无疑点()
    {
        var (uid, _) = SeedDayMidWorld();
        await SyncAsync(At(8, 25), At(12, 20));   // 忘了打下班卡
        using (var db = CreateContext())
            await Svc(db, Tue.AddDays(1).ToDateTime(new TimeOnly(9, 0)))
                .AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(17, 35), false, "忘打卡", "管理员");
        Assert.Empty(await AuditTueAsync());
    }

    [Fact]
    public async Task 工作日加班审批通过_只记下班后的部分_核对无疑点()
    {
        var (uid, _) = SeedWeekWorld();   // 周五到周一排白班 08:30–17:30
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "JB-A-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            OvertimeStartTime = Mon.ToDateTime(new TimeOnly(18, 0)), OvertimeEndTime = Mon.ToDateTime(new TimeOnly(21, 0)), OvertimeDurationHours = 3, Reason = "t"
        });
        using (var db = CreateContext()) await Svc(db).UpdateAttendanceAfterApprovalAsync(id);
        using var db2 = CreateContext();
        Assert.Empty(await Svc(db2).AuditAsync(Mon, Mon));
    }

    [Fact]
    public async Task 工时被改乱_核对报出来()
    {
        var (uid, _) = SeedDayMidWorld();
        await SyncAsync(At(8, 25), At(12, 20), At(17, 35));
        using (var db = CreateContext())
        {
            (await db.AttendanceRecords.SingleAsync(r => r.UserId == uid)).ActualWorkHours = 12m;
            await db.SaveChangesAsync();
        }
        var issues = await AuditTueAsync();
        var hit = Assert.Single(issues, i => i.Type == AttendanceService.AuditHours);
        Assert.Equal(12m, hit.Stored);
        Assert.Equal(8m, hit.Expected);   // 08:30–17:30 扣午饭 1 小时 = 8
    }

    [Fact]
    public async Task 加班记错_多记和少记都报()
    {
        var (uid, _) = SeedWeekWorld();
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "JB-A-2", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            OvertimeStartTime = Mon.ToDateTime(new TimeOnly(18, 0)), OvertimeEndTime = Mon.ToDateTime(new TimeOnly(21, 0)), OvertimeDurationHours = 3, Reason = "t"
        });
        using (var db = CreateContext()) await Svc(db).UpdateAttendanceAfterApprovalAsync(id);

        foreach (var (wrong, expectedType) in new[] { (9m, AttendanceService.AuditOtOver), (0m, AttendanceService.AuditOtUnder) })
        {
            using (var db = CreateContext())
            {
                (await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon)).OvertimeHours = wrong;
                await db.SaveChangesAsync();
            }
            using var db2 = CreateContext();
            Assert.Contains(await Svc(db2).AuditAsync(Mon, Mon), i => i.Type == expectedType);
        }
    }

    [Fact]
    public async Task 有打卡但没排班_核对报出来()
    {
        var (uid, _) = SeedDayMidWorld();
        await SyncAsync(At(8, 25), At(17, 35));
        using (var db = CreateContext())
        {
            db.ShiftAssignments.RemoveRange(db.ShiftAssignments.Where(a => a.UserId == uid));
            await db.SaveChangesAsync();
        }
        Assert.Contains(await AuditTueAsync(), i => i.Type == AttendanceService.AuditNoShift);
    }

    [Fact]
    public async Task 白天打卡却排了夜班_核对报出来()
    {
        var (uid, _) = SeedDayMidWorld();
        await SyncAsync(At(8, 25), At(17, 35));
        using (var db = CreateContext())
        {
            var group = db.Users.Single(u => u.Id == uid).AttendanceGroupId!.Value;
            var night = NightShift(); night.AttendanceGroupId = group;
            db.ShiftSchedules.Add(night); db.SaveChanges();
            db.ShiftAssignments.Single(a => a.UserId == uid && a.WorkDate == Tue).ShiftScheduleId = night.Id;
            await db.SaveChangesAsync();
        }
        Assert.Contains(await AuditTueAsync(), i => i.Type == AttendanceService.AuditShiftMismatch);
    }

    [Fact]
    public async Task 先打卡后补排班_排班后工时按班次重算_核对无疑点()
    {
        var (uid, _) = SeedDayMidWorld();
        int shiftId;
        using (var db = CreateContext())
        {
            // 事故场景：当时没排班（打卡结算没有班次可套 → 不封顶、不套午间窗口），打完卡后才补排班
            shiftId = db.ShiftAssignments.Single(a => a.UserId == uid && a.WorkDate == Tue).ShiftScheduleId;
            db.ShiftAssignments.RemoveRange(db.ShiftAssignments.Where(a => a.UserId == uid));
            await db.SaveChangesAsync();
        }
        await SyncAsync(At(8, 25), At(21, 0));   // 没排班：08:25–21:00 全算，不封顶
        Assert.True((await AuditTueAsync()).Count > 0);   // 没排班的记录，核对会报"没排班"
        using (var db = CreateContext())
        {
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = uid, WorkDate = Tue, ShiftScheduleId = shiftId });
            await db.SaveChangesAsync();
        }
        using (var db = CreateContext())
        {
            // 排上班后：午间窗口没打 → 只算窗口开始之前，和"一开始就排好班"的结果一致；补排班后自动重算
            var changed = await Svc(db).RecalcHoursAfterScheduleAsync([uid], Tue, Tue);
            Assert.Equal(1, changed);
        }
        Assert.Empty(await AuditTueAsync());
        using var check = CreateContext();
        Assert.InRange((await check.AttendanceRecords.SingleAsync(r => r.UserId == uid)).ActualWorkHours, 3m, 3.6m);
    }

    [Fact]
    public async Task 正常排班的记录_补排班重算不改任何数据()
    {
        var (uid, _) = SeedDayMidWorld();
        await SyncAsync(At(8, 25), At(12, 20), At(17, 35));
        using var db = CreateContext();
        Assert.Equal(0, await Svc(db).RecalcHoursAfterScheduleAsync([uid], Tue, Tue));
    }
}
