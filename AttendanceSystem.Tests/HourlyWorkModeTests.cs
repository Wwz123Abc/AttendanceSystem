using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 计时工模式（考勤组 HourlyWorkStartDate，2026-10-20 起用于成都鹰诺深圳分公司临时工）：
/// 工时 = 最后一次下班卡 − 第一次上班卡，扣午饭晚饭，不封顶；没有加班/旷工/迟到早退；午间漏打只算窗口开始之前；
/// 00:00–07:00 的打卡无效；只对开启了的考勤组生效，别的组完全不变。
/// </summary>
public class HourlyWorkModeTests : SqliteTestBase
{
    private static DateTime At(int h, int m) => Tue.ToDateTime(new TimeOnly(h, m));

    private async Task SyncAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await sync.ProcessAttLogAsync("SNM", times.Select(t => new ZKAttLogRow("M1", t, 0, 15)).ToList());
    }

    private (int uid, int groupId) SeedHourly(bool hourlyOn = true)
    {
        var (uid, _) = SeedDayMidWorld();   // 白班、午间窗口 12:00–13:00、考勤机 SNM、工号 M1
        using var db = CreateContext();
        var group = db.AttendanceGroups.Single(g => g.Id == db.Users.Single(u => u.Id == uid).AttendanceGroupId);
        if (hourlyOn) group.HourlyWorkStartDate = Tue;
        db.SaveChanges();
        return (uid, group.Id);
    }

    private async Task<AttendanceRecord> TueRecordAsync(int uid)
    {
        using var db = CreateContext();
        return await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
    }

    [Fact]
    public async Task 午间打上_取最晚一次下班卡_工时不封顶()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20), At(18, 10), At(21, 0));
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(21, 0), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 11m, 11.1m);   // 08:25–21:00 = 12 小时 35 分，扣午饭 1 小时、晚饭 0.5 小时
        Assert.Equal(AttendanceStatus.Normal, rec.AttendanceStatus);
    }

    [Fact]
    public async Task 午间窗口漏打_只算窗口开始之前的工时()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(21, 0));
        var rec = await TueRecordAsync(uid);
        Assert.InRange(rec.ActualWorkHours, 3.5m, 3.6m);   // 08:25–12:00，下午不算
    }

    [Fact]
    public async Task 凌晨0点到7点的打卡无效_不会变成第二天的上班卡()
    {
        var (uid, _) = SeedHourly();
        var wed = Tue.AddDays(1);
        await SyncAsync(At(8, 25), At(12, 20), At(17, 40), wed.ToDateTime(new TimeOnly(0, 30)), wed.ToDateTime(new TimeOnly(6, 59)));
        using var db = CreateContext();
        Assert.False(await db.AttendancePunches.AnyAsync(p => p.UserId == uid && p.PunchTime >= wed.ToDateTime(TimeOnly.MinValue) && p.PunchTime < wed.ToDateTime(new TimeOnly(7, 0))));
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == wed));
        Assert.Equal(At(17, 40), (await TueRecordAsync(uid)).ClockOutTime);   // 凌晨那两次没有被当成下班卡
    }

    [Fact]
    public async Task 当天23点59分的下班卡有效()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20), At(23, 59));
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(23, 59), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 14m, 14.1m);
    }

    [Fact]
    public async Task 计时工上班卡照常判断迟到_但没有早退_工时不受迟到影响()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(10, 0), At(12, 20), At(18, 0));   // 班次 08:30 上班，10:00 才到
        var rec = await TueRecordAsync(uid);
        Assert.Equal(AttendanceStatus.Late, rec.AttendanceStatus);
        Assert.True(rec.LateMinutes > 0);
        Assert.Equal(0, rec.EarlyLeaveMinutes);
        Assert.Equal(6.5m, rec.ActualWorkHours);   // 10:00–18:00 = 8 小时，扣午饭 1 小时、晚饭 0.5 小时（17:30–18:00）；迟到只记录，工时按实际到岗时间算
    }

    [Fact]
    public async Task 只有上班卡和午间卡_最晚一次打卡当下班_午间已打上不扣下午()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20));
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(12, 20), rec.ClockOutTime);                // 最晚一次打卡就是下班时间
        Assert.InRange(rec.ActualWorkHours, 3.5m, 3.6m);           // 08:25–12:20 = 3 小时 55 分，扣午饭重叠的 20 分钟
        Assert.Equal(AttendanceStatus.Normal, rec.AttendanceStatus);
    }

    [Fact]
    public async Task 只有上班卡_没有任何其他打卡_工时为0()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25));
        var rec = await TueRecordAsync(uid);
        Assert.Null(rec.ClockOutTime);
        Assert.Equal(0m, rec.ActualWorkHours);
    }

    [Fact]
    public async Task 没开计时工模式的考勤组_工时仍按班次封顶_完全不变()
    {
        var (uid, _) = SeedHourly(hourlyOn: false);
        await SyncAsync(At(8, 25), At(12, 20), At(18, 10), At(21, 0));
        var rec = await TueRecordAsync(uid);
        Assert.True(rec.ActualWorkHours <= 8.5m, $"普通考勤组工时应按班次封顶，实际 {rec.ActualWorkHours}");
    }

    [Fact]
    public async Task 计时工的发薪日格子不封顶_普通组仍封顶标准工时()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20), At(18, 10), At(21, 0));
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var row = await svc.GetMyPayrollRowAsync(uid, Tue, Tue);
        Assert.Equal(11m, row!.DailyHours[0]);   // 11.08 向下取整到 0.5，不是被封顶的 8
    }

    /// <summary>把当天这条记录改成"请假 2 小时"（模拟请假审批通过后的状态），再走"修改班次时重算历史工时"这条结算路径。</summary>
    private async Task<decimal> HoursAfterLeaveRecalcAsync(int uid, int groupId)
    {
        using (var db = CreateContext())
        {
            var r = await db.AttendanceRecords.SingleAsync(x => x.UserId == uid && x.WorkDate == Tue);
            r.AttendanceStatus = AttendanceStatus.OnLeave;
            r.LeaveHours = 2m;
            await db.SaveChangesAsync();
        }
        using (var db = CreateContext())
        {
            var shiftId = await db.ShiftSchedules.Where(s => s.AttendanceGroupId == groupId).Select(s => s.Id).SingleAsync();
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).RecalcShiftHistoryAsync(shiftId, Tue);
        }
        return (await TueRecordAsync(uid)).ActualWorkHours;
    }

    [Fact]
    public async Task 计时工请假当天_重算工时不按标准工时减请假小时封顶()
    {
        var (uid, gid) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20), At(18, 10), At(21, 30));   // 考勤机直接同步：08:25–21:30 扣 1.5 小时 ≈ 11.5
        var before = (await TueRecordAsync(uid)).ActualWorkHours;
        Assert.InRange(before, 11.5m, 11.6m);
        Assert.Equal(before, await HoursAfterLeaveRecalcAsync(uid, gid));   // 请假审批/补卡/改班次重算之后工时不能掉到 6
    }

    [Fact]
    public async Task 普通考勤组请假当天_重算工时仍按标准工时减请假小时封顶()
    {
        var (uid, gid) = SeedHourly(hourlyOn: false);
        await SyncAsync(At(8, 25), At(12, 20), At(18, 10), At(21, 30));
        Assert.True(await HoursAfterLeaveRecalcAsync(uid, gid) <= 6m);   // 8 小时标准工时 − 2 小时请假
    }

    [Fact]
    public async Task 月度汇总_计时工只打一次卡不算缺卡_休息日来上班算出勤()
    {
        var (uid, gid) = SeedHourly();
        await SyncAsync(At(8, 25));   // 只有一张上班卡
        var sat = Tue.AddDays(4);     // 班次配置的休息日（周六）
        using (var db = CreateContext())
        {
            var shiftId = await db.ShiftSchedules.Where(s => s.AttendanceGroupId == gid).Select(s => s.Id).SingleAsync();
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = uid, WorkDate = sat, ShiftScheduleId = shiftId });
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = sat, ClockInTime = sat.ToDateTime(new TimeOnly(8, 30)), ClockOutTime = sat.ToDateTime(new TimeOnly(17, 30)),
                ActualWorkHours = 8m, AttendanceStatus = AttendanceStatus.Normal
            });
            await db.SaveChangesAsync();
        }
        using var db2 = CreateContext();
        await new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance).GenerateMonthlySummaryAsync(Tue.Year, Tue.Month, [uid]);
        var s = await db2.MonthlyAttendanceSummaries.SingleAsync(x => x.UserId == uid);
        Assert.Equal(0, s.NotPunchedCount);                 // 计时工没有缺卡
        Assert.Equal(2m, (decimal)s.ActualWorkdays);        // 周二 + 休息日周六都算出勤
    }

    [Fact]
    public async Task 计时工提交加班申请被拦住()
    {
        var (uid, _) = SeedHourly();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Overtime, Reason = "t",
            OvertimeStartTime = At(18, 0), OvertimeEndTime = At(21, 0)
        }));
        Assert.Contains("计时工", ex.Message);
    }

    [Fact]
    public void 计时工的打卡类型_第一次上班_窗口内午间_其余下班()
    {
        var shift = DayShift(); shift.MidCheckWindows = "12:00-13:00";
        Assert.Equal(PunchType.ClockIn,  AttendanceService.ResolveHourlyPunchType(At(8, 20),  Tue, shift, false));
        Assert.Equal(PunchType.MidCheck, AttendanceService.ResolveHourlyPunchType(At(12, 30), Tue, shift, true));
        Assert.Equal(PunchType.MidCheck, AttendanceService.ResolveHourlyPunchType(At(13, 20), Tue, shift, true));   // 窗口结束后 30 分钟宽限内
        Assert.Equal(PunchType.ClockOut, AttendanceService.ResolveHourlyPunchType(At(13, 45), Tue, shift, true));
        Assert.Equal(PunchType.ClockOut, AttendanceService.ResolveHourlyPunchType(At(18, 0),  Tue, shift, true));   // 下班时间不固定，傍晚的卡也是下班
        Assert.Equal(PunchType.ClockOut, AttendanceService.ResolveHourlyPunchType(At(10, 0),  Tue, shift, true));   // 上午随手打的卡暂记下班，被之后更晚的覆盖
    }

    private async Task<PunchResponseDto> PunchAtAsync(int uid, DateTime when, PunchType type)
    {
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance, new FixedClock(when));
        return await svc.PunchAsync(uid, new PunchRequestDto { PunchType = type }, skipLocationCheck: true);
    }

    [Fact]
    public async Task 手机远程打卡_凌晨无效_白天正常_多次下班卡取最晚()
    {
        var (uid, _) = SeedHourly();
        var wed = Tue.AddDays(1);
        var night = await PunchAtAsync(uid, wed.ToDateTime(new TimeOnly(2, 30)), PunchType.ClockIn);
        Assert.False(night.Success);
        Assert.Contains("07:00", night.Message);

        Assert.True((await PunchAtAsync(uid, At(8, 20), PunchType.ClockIn)).Success);
        Assert.True((await PunchAtAsync(uid, At(12, 40), PunchType.MidCheck)).Success);
        Assert.True((await PunchAtAsync(uid, At(17, 0), PunchType.ClockOut)).Success);
        Assert.True((await PunchAtAsync(uid, At(20, 30), PunchType.ClockOut)).Success);   // 又打了一次，取更晚的
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(20, 30), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 10.5m, 10.6m);   // 08:20–20:30 = 12h10m − 1h − 0.5h
        Assert.Equal(AttendanceStatus.Normal, rec.AttendanceStatus);
        using var db = CreateContext();
        Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == wed));   // 凌晨那次没有生成第二天的记录
    }

    private AttendanceService Svc(AttendanceDbContext db) => new(db, AppOptions, NullLogger<AttendanceService>.Instance);

    [Fact]
    public async Task 只有上班卡和午间卡后补下班卡_补的时间比午间卡晚才有效_工时重新算到补的下班时间()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20));   // 下班时间暂时记成最晚一次打卡 12:20
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(18, 0), false, null, "管理员");
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(18, 0), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 7.5m, 8m);   // 08:25–18:00 = 9 小时 35 分，扣午饭 1 小时、晚饭 0.5 小时 = 8.08，系统存的是向下取整前的值
        using var db2 = CreateContext();
        // 补的时间比现有"下班时间"（12:20）还早：不生效，仍按最晚的算
        await Assert.ThrowsAsync<BusinessException>(() =>
            Svc(db2).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, At(11, 0), false, null, "管理员"));
    }

    [Fact]
    public async Task 计时工补卡不能补到第二天凌晨()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(12, 20));
        using var db = CreateContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.ClockOut, Tue.AddDays(1).ToDateTime(new TimeOnly(0, 30)), false, null, "管理员"));
        Assert.Contains("23:59", ex.Message);
    }

    [Fact]
    public async Task 午间漏打扣下午_补了午间卡后正常算全天工时()
    {
        var (uid, _) = SeedHourly();
        await SyncAsync(At(8, 25), At(21, 0));   // 没打午间卡：只算 08:25–12:00
        Assert.InRange((await TueRecordAsync(uid)).ActualWorkHours, 3.5m, 3.6m);
        using (var db = CreateContext())
            await Svc(db).AdminReplenishPunchAsync(uid, Tue, PunchType.MidCheck, At(12, 20), false, null, "管理员");
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(21, 0), rec.ClockOutTime);
        Assert.InRange(rec.ActualWorkHours, 11m, 11.1m);   // 补了午间卡：08:25–21:00 扣 1.5 小时，全天工时
    }

    [Fact]
    public async Task 历史重建_按原始打卡重算_加班清零_旷工改正常_补卡时间保留_先预览不写库()
    {
        var (uid, gid) = SeedHourly();
        var mon = Tue.AddDays(-1);
        using (var db = CreateContext())
        {
            // 周二：原来按班次封顶 8 小时 + 加班 3，下班时间被记到第二天凌晨；真实打卡 08:25 / 12:20 / 21:00
            db.AttendancePunches.AddRange(
                new AttendancePunch { UserId = uid, PunchTime = At(8, 25), PunchType = PunchType.ClockIn, IsValid = true },
                new AttendancePunch { UserId = uid, PunchTime = At(12, 20), PunchType = PunchType.MidCheck, IsValid = true },
                new AttendancePunch { UserId = uid, PunchTime = At(21, 0), PunchType = PunchType.ClockOut, IsValid = true });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, ClockInTime = At(8, 25), ClockOutTime = Tue.AddDays(1).ToDateTime(new TimeOnly(0, 30)),
                AttendanceStatus = AttendanceStatus.EarlyLeave, ActualWorkHours = 8m, OvertimeHours = 3m, MidCheckResults = "12:00-13:00=12:20" });
            // 周一：没有任何打卡，被记旷工，还挂着加班
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = mon, AttendanceStatus = AttendanceStatus.Absent, OvertimeHours = 2m });
            db.SaveChanges();
        }

        using (var db = CreateContext())   // 预览：不写库
        {
            var rows = await Svc(db).RebuildHourlyRecordsAsync([gid], mon, Tue, dryRun: true);
            Assert.Equal(2, rows.Count);
            Assert.InRange(rows.Single(r => r.WorkDate == Tue).NewHours, 11m, 11.1m);
        }
        Assert.Equal(8m, (await TueRecordAsync(uid)).ActualWorkHours);   // 还是旧的

        using (var db = CreateContext())
            await Svc(db).RebuildHourlyRecordsAsync([gid], mon, Tue, dryRun: false);
        var rec = await TueRecordAsync(uid);
        Assert.Equal(At(21, 0), rec.ClockOutTime);                       // 凌晨 00:30 那个无效，改成真实最晚打卡
        Assert.InRange(rec.ActualWorkHours, 11m, 11.1m);                // 不封顶
        Assert.Equal(0m, rec.OvertimeHours);                            // 计时工没有加班
        Assert.Equal("12:00-13:00=12:20", rec.MidCheckResults);
        using var chk = CreateContext();
        var m = await chk.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == mon);
        Assert.Equal(AttendanceStatus.Normal, m.AttendanceStatus);      // 没有旷工一说
        Assert.Equal(0m, m.OvertimeHours);
        Assert.Equal(0m, m.ActualWorkHours);
    }
}
