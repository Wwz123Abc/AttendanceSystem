using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AttendanceSystem.Models.Exceptions;

namespace AttendanceSystem.Tests;

/// <summary>
/// 跨天打卡归属：白班加班过零点、补卡跨天、没排班的人不被"顺延"规则多算、管理员手动补卡校验。
/// </summary>
public class CrossMidnightPunchTests : SqliteTestBase
{
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
            var ioe = Assert.IsType<BusinessException>(ex);
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
    public async Task 管理员手动补卡_打卡时间不在考勤日当天或第二天_直接拒绝_下班早于上班也拒绝()
    {
        var (uid, _) = SeedWeekWorld();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var wrongMonth = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.AdminAdjustPunchAsync(uid, Mon, new DateTime(2026, 8, 7, 8, 30, 0), null, null, "管理员"));
        Assert.Contains("请检查日期", wrongMonth.Message);
        var reversed = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.AdminAdjustPunchAsync(uid, Mon, Mon.ToDateTime(new TimeOnly(17, 0)), Mon.ToDateTime(new TimeOnly(8, 0)), null, "管理员"));
        Assert.Contains("下班时间必须晚于上班时间", reversed.Message);
        // 夜班下班在第二天：允许
        await svc.AdminAdjustPunchAsync(uid, Mon, Mon.ToDateTime(new TimeOnly(20, 0)), Tue.ToDateTime(new TimeOnly(8, 0)), null, "管理员");
    }

    // ── 补卡审批：夜班下班卡顺延到第二天后，仍不能是"还没到"的未来时间点 ──────────────
    // （2026-09-29 第 12 轮审查发现：上一轮 M5 只在提交时比较"申请日期+申请时间"，没考虑夜班顺延；
    // 顺延是在审批通过时才发生的，提交时早于现在的申请，顺延后可能变成未来——两处都要按同一套
    // 顺延规则重新算一遍再比较，用动态的"今天/昨天/前天"而不是固定历史日期，因为这条 bug 的本质
    // 就是"结果是否晚于真实的 DateTime.Now"）

    [Fact]
    public async Task 补卡申请_夜班下班卡顺延后落在未来_提交时直接拒绝()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (uid, _) = SeedNightWorldForFutureCheck(today);   // 今天排的是跨天夜班，一次卡都没打
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        // 没有上班卡、班次跨天、06:00 早于 20:00 的上班时间 → 顺延到"明天 06:00"——不管现在几点，
        // 明天都还没到，这是本条 bug 的核心场景
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.PunchReplenishment,
            PunchDate = today, PunchType = PunchType.ClockOut, PunchTime = new TimeOnly(6, 0),
            Reason = "t"
        }));
        Assert.Contains("还没到", ex.Message);
    }

    [Fact]
    public async Task 补卡申请_夜班下班卡顺延后落在过去_提交正常通过()
    {
        var twoDaysAgo = DateOnly.FromDateTime(DateTime.Today).AddDays(-2);
        var (uid, _) = SeedNightWorldForFutureCheck(twoDaysAgo);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        // 顺延到"前天+1天=昨天 06:00"——不管现在几点，昨天一定已经过去
        var request = await svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.PunchReplenishment,
            PunchDate = twoDaysAgo, PunchType = PunchType.ClockOut, PunchTime = new TimeOnly(6, 0),
            Reason = "t"
        });
        Assert.NotNull(request);
    }

    [Fact]
    public async Task 补卡审批通过回写_顺延结果晚于现在_审批失败_考勤记录不变()
    {
        // 提交时的检查只堵了"提交那一刻"：如果提交时上班卡还没补上（顺延结果算出来是过去），
        // 审批人拖到"顺延结果变成未来"才点通过，审批回写这一步要独立兜底拦下来
        var tomorrow = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        var (uid, _) = SeedNightWorldForFutureCheck(tomorrow);
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "BK-FUT-1", ApplicantUserId = uid, ApprovalType = ApprovalType.PunchReplenishment,
            PunchDate = tomorrow, PunchType = PunchType.ClockOut, PunchTime = new TimeOnly(6, 0), Reason = "t"
        });

        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.UpdateAttendanceAfterApprovalAsync(id));
            Assert.Contains("还没到", ex.Message);
        }

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == tomorrow));   // 没有留下改了一半的记录
    }

    [Fact]
    public async Task 审批被顺延校验拦下后_同一上下文里刷新列表看到的仍是待审批_不是已通过()
    {
        // 2026-09-29 第 13 轮审查发现：HandleApprovalAsync 在事务里先把 req.ApprovalStatus 改成
        // "已通过"（内存里的跟踪实体），UpdateAttendanceAfterApprovalAsync 抛异常后事务回滚，
        // 数据库确实还是"待审批"，但同一个 DbContext 里这个已跟踪对象的内存状态没有跟着回滚——
        // PendingApproval 页面用同一个 DbContext 刷新列表时，会读到这个内存里"改了一半"的对象，
        // 显示成"已通过"，需要 db.ChangeTracker.Clear() 才能让后续查询重新从数据库读。
        var tomorrow = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        var (uid, supervisorId) = SeedNightWorldForFutureCheck(tomorrow);

        int requestId;
        using (var db = CreateContext())
        {
            var request = new ApprovalRequest
            {
                RequestNo = "BK-FUT-2", ApplicantUserId = uid, ApprovalType = ApprovalType.PunchReplenishment,
                ApprovalStatus = ApprovalStatus.Pending, PunchDate = tomorrow, PunchType = PunchType.ClockOut,
                PunchTime = new TimeOnly(6, 0), Reason = "t", SubmittedAt = DateTime.Now, UpdatedAt = DateTime.Now
            };
            db.ApprovalRequests.Add(request);
            await db.SaveChangesAsync();
            db.ApprovalSteps.Add(new ApprovalStep
            {
                ApprovalRequestId = request.Id, ApproverUserId = supervisorId, StepOrder = 1,
                ApprovalStatus = ApprovalStatus.Pending
            });
            await db.SaveChangesAsync();
            requestId = request.Id;
        }

        using var check = CreateContext();
        var att = new AttendanceService(check, AppOptions, NullLogger<AttendanceService>.Instance);
        var svc = new ApprovalService(check, att, AppOptions);

        await Assert.ThrowsAsync<BusinessException>(() => svc.HandleApprovalAsync(supervisorId,
            new HandleApprovalDto { ApprovalRequestId = requestId, IsApproved = true }));

        // 不清空的话，同一上下文再查这张单，读到的是内存里"改到一半"的实例（已通过）——
        // 这行断言本身不是在验证 bug 修没修，只是确认"如果不清空会看到什么"，帮助理解下面为什么要 Clear
        var staleRead = await check.ApprovalRequests.FirstAsync(a => a.Id == requestId);
        Assert.Equal(ApprovalStatus.Approved, staleRead.ApprovalStatus);   // 内存里确实是脏的

        check.ChangeTracker.Clear();
        var freshRead = await check.ApprovalRequests.FirstAsync(a => a.Id == requestId);
        Assert.Equal(ApprovalStatus.Pending, freshRead.ApprovalStatus);   // 清空后重新从数据库读，是真实的"待审批"
    }
}
