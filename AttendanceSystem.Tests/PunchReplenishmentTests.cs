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
/// 补卡（上班 / 下班 / 午间）：补上班卡只有更早才生效、补下班卡只有更晚才生效、午间补卡只新增一条午间打卡记录、
/// 时间要放在正确的位置（班次、前一天的下班、下一个上班）。
/// </summary>
public class PunchReplenishmentTests : SqliteTestBase
{
    private static DateTime At(DateOnly d, int h, int m) => d.ToDateTime(new TimeOnly(h, m));

    private static ReplenishmentContext Ctx(ShiftSchedule? shift, DateTime? ci = null, DateTime? co = null,
        DateTime? prevOut = null, DateTime? next = null, params DateTime[] punches)
        => new(shift, ci, co, prevOut, next, punches.ToList());

    private static ShiftSchedule DayWithWindow(string windows = "12:00-13:00")
    {
        var s = DayShift(); s.MidCheckWindows = windows; return s;
    }

    // ── 规则本身（纯函数）──────────────────────────────────────────────

    [Fact]
    public void 补上班卡_只有比现有上班时间更早才有效()
    {
        var ctx = Ctx(DayWithWindow(), ci: At(Tue, 8, 25), co: At(Tue, 17, 40));
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, At(Tue, 8, 0), ctx));
        var same  = PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, At(Tue, 8, 25), ctx);
        var later = PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, At(Tue, 12, 40), ctx);   // 中午那一次误选成上班卡
        Assert.Contains("更早", same);
        Assert.Contains("更早", later);
        Assert.Contains("午间打卡", later);                                                              // 提示要改选"午间打卡"
    }

    [Fact]
    public void 补下班卡_只有比现有下班时间更晚才有效()
    {
        var ctx = Ctx(DayWithWindow(), ci: At(Tue, 8, 25), co: At(Tue, 17, 40));
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.ClockOut, Tue, At(Tue, 18, 30), ctx));
        Assert.Contains("更晚", PunchReplenishmentRules.FindProblem(PunchType.ClockOut, Tue, At(Tue, 17, 40), ctx));
        Assert.Contains("更晚", PunchReplenishmentRules.FindProblem(PunchType.ClockOut, Tue, At(Tue, 12, 40), ctx));   // 中午误选成下班卡
    }

    [Fact]
    public void 没有现成的上下班卡_补卡不受更早更晚的限制()
    {
        var ctx = Ctx(DayWithWindow());
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, At(Tue, 8, 30), ctx));
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.ClockOut, Tue, At(Tue, 17, 30), ctx));
    }

    [Fact]
    public void 补上班卡_不能早于前一天的下班卡_也不能晚于当天已有的下班卡()
    {
        var ctx = Ctx(DayWithWindow(), co: At(Tue, 17, 40), prevOut: At(Tue, 8, 0));   // 前一天夜班今早 08:00 才下班
        Assert.Contains("前一天的下班卡", PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, At(Tue, 7, 30), ctx));
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, At(Tue, 8, 30), ctx));
        Assert.Contains("不能晚于", PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, At(Tue, 18, 0), ctx));
    }

    [Fact]
    public void 补下班卡_不能晚于下一个上班的时间()
    {
        var ctx = Ctx(DayWithWindow(), ci: At(Tue, 8, 25), next: At(Tue.AddDays(1), 7, 30));   // 明天最早 07:30 可以打上班卡
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.ClockOut, Tue, At(Tue, 22, 0), ctx));
        Assert.Contains("下一个上班", PunchReplenishmentRules.FindProblem(PunchType.ClockOut, Tue, At(Tue.AddDays(1), 7, 40), ctx));
    }

    [Fact]
    public void 午间补卡_必须有排班_班次要配了窗口_要先有上班卡()
    {
        var at = At(Tue, 12, 30);
        Assert.Contains("没有排班", PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, at, Ctx(null, ci: At(Tue, 8, 25))));
        Assert.Contains("没有设置午间", PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, at, Ctx(DayShift(), ci: At(Tue, 8, 25))));
        Assert.Contains("先补上班卡", PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, at, Ctx(DayWithWindow())));
    }

    [Fact]
    public void 午间补卡_必须落在没命中的窗口里_夹在上班卡和下班卡之间()
    {
        var shift = DayWithWindow("12:00-13:00");
        var ctx = Ctx(shift, ci: At(Tue, 8, 25), co: At(Tue, 17, 40), punches: At(Tue, 13, 40));   // 13:40 在窗口外，窗口还是空的
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, At(Tue, 12, 30), ctx));
        Assert.Contains("午间窗口内", PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, At(Tue, 13, 45), ctx));   // 不在窗口内（窗口结束后 30 分钟的宽限也过了）
        Assert.Contains("要晚于这天的上班卡", PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, At(Tue, 8, 0), Ctx(shift, ci: At(Tue, 8, 25))));
        Assert.Contains("要早于这天的下班卡", PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, At(Tue, 12, 30), Ctx(shift, ci: At(Tue, 8, 25), co: At(Tue, 12, 10))));

        var hit = Ctx(shift, ci: At(Tue, 8, 25), co: At(Tue, 17, 40), punches: At(Tue, 12, 15));   // 窗口已经命中
        Assert.Contains("已经有午间打卡", PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, At(Tue, 12, 30), hit));
    }

    [Fact]
    public void 午间补卡_夜班窗口在午夜之后_时间按班次推算到第二天()
    {
        var night = NightShift(); night.MidCheckWindows = "00:00-01:00";   // 20:00 上班，午间窗口在次日凌晨
        var ci = At(Tue, 20, 5);
        var resolved = PunchReplenishmentRules.ResolveTime(PunchType.MidCheck, Tue, new TimeOnly(0, 30), ci, night);
        Assert.Equal(At(Tue.AddDays(1), 0, 30), resolved);
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, resolved, Ctx(night, ci: ci)));
        // 同一个钟点如果误当成当天 00:30（比上班卡还早），一定过不了
        Assert.NotNull(PunchReplenishmentRules.FindProblem(PunchType.MidCheck, Tue, At(Tue, 0, 30), Ctx(night, ci: ci)));
    }

    // ── 端到端：审批通过后的回写 ─────────────────────────────────────────

    /// <summary>白班（午间窗口 12:00–13:00）：08:25 上班、13:40 午间（超出窗口）、17:40 下班，走真实的考勤机同步。</summary>
    private async Task<(int uid, int supervisorId)> SeedMissedWindowDay()
    {
        var (uid, _) = SeedDayMidWorld();
        int supId;
        using (var db = CreateContext())
        {
            var sup = new User { EmployeeNo = "SUPM", RealName = "上级", PasswordHash = "x", IsActive = true, Role = UserRole.Supervisor };
            db.Users.Add(sup);
            await db.SaveChangesAsync();
            (await db.Users.FindAsync(uid))!.SupervisorUserId = sup.Id;
            await db.SaveChangesAsync();
            supId = sup.Id;
        }
        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await svc.ProcessAttLogAsync("SNM", [
                new ZKAttLogRow("M1", At(Tue, 8, 25), 0, 15),
                new ZKAttLogRow("M1", At(Tue, 13, 40), 0, 15),
                new ZKAttLogRow("M1", At(Tue, 17, 40), 0, 15)]);
        }
        return (uid, supId);
    }

    private async Task<AttendanceRecord> Approve(int uid, PunchType type, int hour, int minute)
    {
        using var db = CreateContext();
        var ap = new ApprovalRequest
        {
            RequestNo = $"BK-{type}-{hour}{minute}", ApplicantUserId = uid, ApprovalType = ApprovalType.PunchReplenishment,
            ApprovalStatus = ApprovalStatus.Approved, PunchDate = Tue, PunchType = type, PunchTime = new TimeOnly(hour, minute),
            Reason = "t", SubmittedAt = DateTime.Now, UpdatedAt = DateTime.Now
        };
        db.ApprovalRequests.Add(ap);
        await db.SaveChangesAsync();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        await svc.UpdateAttendanceAfterApprovalAsync(ap.Id);
        return await db.AttendanceRecords.AsNoTracking().SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
    }

    [Fact]
    public async Task 午间补卡通过_只新增一条午间打卡_上下班时间不动_午间命中和工时重新算()
    {
        var (uid, _) = await SeedMissedWindowDay();
        using (var db = CreateContext())
        {
            var before = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
            Assert.Equal(At(Tue, 8, 25), before.ClockInTime);
            Assert.Equal(At(Tue, 17, 40), before.ClockOutTime);
            Assert.Null(before.MidCheckResults.ParseMidCheckResults().Single().HitTime);   // 13:40 超出窗口：午间漏打
            Assert.True(before.ActualWorkHours < 8m);                                      // 漏打午间，只算半天
        }
        var punchesBefore = await CreateContext().AttendancePunches.CountAsync(p => p.UserId == uid);

        var after = await Approve(uid, PunchType.MidCheck, 12, 30);

        Assert.Equal(At(Tue, 8, 25), after.ClockInTime);                                   // 上班时间没被覆盖
        Assert.Equal(At(Tue, 17, 40), after.ClockOutTime);                                 // 下班时间没被覆盖
        Assert.Equal(new TimeOnly(12, 30), after.MidCheckResults.ParseMidCheckResults().Single().HitTime);
        Assert.Equal(8m, after.ActualWorkHours);                                           // 工时恢复成整天
        Assert.Equal(AttendanceStatus.Normal, after.AttendanceStatus);
        Assert.Contains("午间补卡", after.ApprovalNote);
        using var chk = CreateContext();
        Assert.Equal(punchesBefore + 1, await chk.AttendancePunches.CountAsync(p => p.UserId == uid));
        var added = await chk.AttendancePunches.SingleAsync(p => p.UserId == uid && p.PunchType == PunchType.MidCheck && p.PunchTime == At(Tue, 12, 30));
        Assert.StartsWith("补卡审批:", added.DeviceInfo);
    }

    [Fact]
    public async Task 午间补卡_窗口已经命中_审批不能通过()
    {
        var (uid, _) = await SeedMissedWindowDay();
        await Approve(uid, PunchType.MidCheck, 12, 30);                                    // 第一张通过，窗口命中了
        await Assert.ThrowsAsync<BusinessException>(() => Approve(uid, PunchType.MidCheck, 12, 45));
    }

    [Fact]
    public async Task 补上班卡_比现有的晚_审批通过但不生效_已有上班时间不被覆盖()
    {
        var (uid, _) = await SeedMissedWindowDay();
        var after = await Approve(uid, PunchType.ClockIn, 12, 40);                         // 员工把中午那次误补成上班卡
        Assert.Equal(At(Tue, 8, 25), after.ClockInTime);
        Assert.Equal(At(Tue, 17, 40), after.ClockOutTime);
        Assert.Contains("未生效", after.ApprovalNote);
        Assert.Equal(AttendanceStatus.Normal, after.AttendanceStatus);                     // 没被改成"迟到 250 分钟"
    }

    [Fact]
    public async Task 补下班卡_比现有的早_审批通过但不生效_已有下班时间不被覆盖()
    {
        var (uid, _) = await SeedMissedWindowDay();
        var after = await Approve(uid, PunchType.ClockOut, 12, 40);
        Assert.Equal(At(Tue, 8, 25), after.ClockInTime);
        Assert.Equal(At(Tue, 17, 40), after.ClockOutTime);
        Assert.Contains("未生效", after.ApprovalNote);
        Assert.Equal(AttendanceStatus.Normal, after.AttendanceStatus);                     // 没被改成"早退 290 分钟"
    }

    [Fact]
    public async Task 补下班卡_比现有的晚_正常生效()
    {
        var (uid, _) = await SeedMissedWindowDay();
        var after = await Approve(uid, PunchType.ClockOut, 18, 30);
        Assert.Equal(At(Tue, 8, 25), after.ClockInTime);
        Assert.Equal(At(Tue, 18, 30), after.ClockOutTime);
    }

    // ── 提交申请时的拦截 ────────────────────────────────────────────────

    [Fact]
    public async Task 提交补卡_不会生效的直接拒绝_午间卡落在窗口内可以提交_同一时间点不能重复提交()
    {
        var (uid, _) = await SeedMissedWindowDay();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        SubmitApprovalDto Dto(PunchType t, int h, int m) => new()
        {
            ApprovalType = ApprovalType.PunchReplenishment, PunchDate = Tue, PunchType = t, PunchTime = new TimeOnly(h, m), Reason = "t"
        };

        var late = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, Dto(PunchType.ClockIn, 12, 40)));
        Assert.Contains("更早", late.Message);                                              // 中午那次选成上班卡，提交就被拦
        var early = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, Dto(PunchType.ClockOut, 12, 40)));
        Assert.Contains("更晚", early.Message);
        var outside = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, Dto(PunchType.MidCheck, 13, 40)));
        Assert.Contains("午间窗口内", outside.Message);

        var ok = await svc.SubmitApprovalAsync(uid, Dto(PunchType.MidCheck, 12, 30));
        Assert.Equal(PunchType.MidCheck, ok.PunchType);
        var dup = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, Dto(PunchType.MidCheck, 12, 30)));
        Assert.Contains("已经提交过", dup.Message);
    }

    [Fact]
    public void 补卡类型的中文名和申请内容描述()
    {
        Assert.Equal("上班", PunchReplenishmentRules.TypeText(PunchType.ClockIn));
        Assert.Equal("下班", PunchReplenishmentRules.TypeText(PunchType.ClockOut));
        Assert.Equal("午间", PunchReplenishmentRules.TypeText(PunchType.MidCheck));
        var dto = new ApprovalRequestDto { ApprovalType = ApprovalType.PunchReplenishment, PunchType = PunchType.MidCheck, PunchDate = Tue, PunchTime = new TimeOnly(12, 30) };
        Assert.Equal("补午间卡 2026-09-08 12:30", dto.ContentText);
    }

    // ── 计时工夜班补卡（2026-11-03 用户要求：放宽到"次日 18:00 前"，与夜班工时上限口径对齐）────────

    /// <summary>造一个"计时工夜班"的补卡现场：班次 20:00–次日 08:00，上班日 19:40 已打上班卡。</summary>
    private static ReplenishmentContext NightHourlyCtx(DateOnly date, DateTime? clockIn = null)
        => new(NightShift(), clockIn ?? date.ToDateTime(new TimeOnly(19, 40)), null, null, null, [], Hourly: true);

    [Theory]
    [InlineData(2026, 11, 2, 8, 0, true)]    // 旧口径（11-03 之前的考勤日）：次日 08:00 可以
    [InlineData(2026, 11, 2, 9, 59, true)]   // 旧口径：次日 09:59 仍可以
    [InlineData(2026, 11, 2, 10, 0, false)]  // 旧口径：次日 10:00 是边界，不含
    [InlineData(2026, 11, 2, 12, 0, false)]  // 旧口径：次日中午不行
    [InlineData(2026, 11, 3, 8, 0, true)]    // 新口径：次日 08:00 可以
    [InlineData(2026, 11, 3, 17, 0, true)]   // 新口径：次日 17:00 可以
    [InlineData(2026, 11, 3, 18, 0, false)]  // 新口径：次日 18:00 是边界，不含
    public void 夜班计时工补下班卡_次日上限按考勤日切换(int y, int m, int d, int hour, int minute, bool ok)
    {
        var date = new DateOnly(y, m, d);
        var ctx = NightHourlyCtx(date);
        var punchAt = date.AddDays(1).ToDateTime(new TimeOnly(hour, minute));
        var problem = PunchReplenishmentRules.FindProblem(PunchType.ClockOut, date, punchAt, ctx);
        Assert.Equal(ok, problem is null);
        if (!ok) Assert.Contains("次日", problem);   // 拒绝时要说明次日上限
    }

    [Fact]
    public void 夜班计时工_上班日19点之前不能补()
    {
        var ctx = NightHourlyCtx(Tue, clockIn: null);
        Assert.Contains("19:00", PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, At(Tue, 18, 0), ctx));
        Assert.Null(PunchReplenishmentRules.FindProblem(PunchType.ClockIn, Tue, At(Tue, 19, 0), ctx));
    }

    [Fact]
    public void 白班计时工仍然不能补次日的卡()
    {
        var ctx = new ReplenishmentContext(DayShift(), At(Tue, 8, 30), null, null, null, [], Hourly: true);
        var problem = PunchReplenishmentRules.FindProblem(PunchType.ClockOut, Tue, At(Tue.AddDays(1), 8, 0), ctx);
        Assert.NotNull(problem);
        Assert.Contains("当天", problem);
    }
}
