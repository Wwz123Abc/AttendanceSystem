using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 请假/出差规则：按班次算时长、跨周末的假别口径、时长上限、开始时间最多提前 3 个月、出差下限。
/// </summary>
public class LeaveAndTripRuleTests : SqliteTestBase
{
    // ── ① 请假时长按班次算 ─────────────────────────────────────────────

    [Fact]
    public void 请假_周一下午到周二中午_按班次时间算_合计约一天_不再是两天()
    {
        var shift = DayShift();
        var start = Mon.ToDateTime(new TimeOnly(13, 30));
        var end   = Tue.ToDateTime(new TimeOnly(12, 0));
        Assert.Equal(4m,   AttendanceService.ComputeLeaveHoursForDay(Mon, start, end, 8, shift));   // 13:30~17:30
        Assert.Equal(3.5m, AttendanceService.ComputeLeaveHoursForDay(Tue, start, end, 8, shift));   // 08:30~12:00
        // 没排班的日子没有班次可参照，仍按自然日算（老口径，封顶在标准工时）
        Assert.Equal(8m, AttendanceService.ComputeLeaveHoursForDay(Mon, start, end, 8));
    }

    [Fact]
    public void 请假_结束时间早于班次上班时间_当天没有交集()
    {
        var shift = DayShift();
        var start = Mon.ToDateTime(new TimeOnly(13, 30));
        var end   = Tue.ToDateTime(new TimeOnly(8, 0));   // 周二 08:00 结束，班次 08:30 才开始
        Assert.False(AttendanceService.HasLeaveOverlapForDay(Tue, start, end, shift));
        Assert.True(AttendanceService.HasLeaveOverlapForDay(Mon, start, end, shift));
    }

    [Fact]
    public void 请假_夜班请一晚_只落在当晚那一天()
    {
        var shift = NightShift();
        var start = Mon.ToDateTime(new TimeOnly(20, 0));
        var end   = Tue.ToDateTime(new TimeOnly(8, 0));
        Assert.Equal(10.5m, AttendanceService.ComputeLeaveHoursForDay(Mon, start, end, 11, shift));
        Assert.False(AttendanceService.HasLeaveOverlapForDay(Tue, start, end, shift));   // 周二排的是周二晚上的班，跟这次假不重叠
    }

    [Theory]
    [InlineData(LeaveType.PersonalLeave, false)]
    [InlineData(LeaveType.SickLeave, false)]
    [InlineData(LeaveType.AnnualLeave, false)]
    [InlineData(LeaveType.CompensatoryLeave, false)]
    [InlineData(LeaveType.MarriageLeave, true)]
    [InlineData(LeaveType.MaternityLeave, true)]
    [InlineData(LeaveType.BereavementLeave, true)]
    public void 婚假产假丧假按自然日_其余假别只算工作日(LeaveType type, bool natural)
        => Assert.Equal(natural, AttendanceService.LeaveCountsNaturalDays(type));

    [Theory]
    [InlineData(LeaveType.PersonalLeave, false)]   // 事假：周末不算请假
    [InlineData(LeaveType.MarriageLeave, true)]    // 婚假：按自然日，周末也算
    public async Task 审批通过回写_跨周末请假_事假不含周末_婚假含周末(LeaveType type, bool weekendCounts)
    {
        var (uid, _) = SeedWeekWorld();
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "QJ-T-" + type, ApplicantUserId = uid, ApprovalType = ApprovalType.Leave, LeaveType = type,
            LeaveStartTime = Fri.ToDateTime(new TimeOnly(13, 30)), LeaveEndTime = Mon.ToDateTime(new TimeOnly(12, 0)), Reason = "t"
        });

        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);

        using var check = CreateContext();
        var recs = await check.AttendanceRecords.Where(r => r.UserId == uid).ToDictionaryAsync(r => r.WorkDate);
        Assert.Equal(4m,   recs[Fri].LeaveHours);   // 周五 13:30~17:30，不是"一直算到午夜 = 整天"
        Assert.Equal(3.5m, recs[Mon].LeaveHours);   // 周一 08:30~12:00，不是"从 0 点起 = 整天"
        Assert.Equal(AttendanceStatus.OnLeave, recs[Fri].AttendanceStatus);
        Assert.Equal(weekendCounts, recs.ContainsKey(Sat));
        Assert.Equal(weekendCounts, recs.ContainsKey(Sun));
        if (weekendCounts) Assert.Equal(8m, recs[Sat].LeaveHours);
    }

    [Fact]
    public async Task 审批通过回写_出差跨周末_周末不算出差()
    {
        var (uid, _) = SeedWeekWorld();
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = "CC-T-1", ApplicantUserId = uid, ApprovalType = ApprovalType.BusinessTrip,
            BusinessTripStartTime = Fri.ToDateTime(new TimeOnly(8, 30)), BusinessTripEndTime = Mon.ToDateTime(new TimeOnly(17, 30)), Reason = "t"
        });

        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);

        using var check = CreateContext();
        var days = await check.AttendanceRecords.Where(r => r.UserId == uid && r.AttendanceStatus == AttendanceStatus.BusinessTrip)
            .Select(r => r.WorkDate).ToListAsync();
        Assert.Equivalent(new[] { Fri, Mon }, days);
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && (r.WorkDate == Sat || r.WorkDate == Sun)));
    }

    // ── 请假/出差：开始时间最多只能提前3个月申请（2026-09-30 用户确认，避免选到离谱的未来日期）──

    [Fact]
    public async Task 请假_开始时间超过3个月后_提交时被拒绝()
    {
        var (uid, _) = SeedWeekWorld();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Now.AddMonths(3).AddDays(1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.PersonalLeave,
            LeaveStartTime = start, LeaveEndTime = start.AddHours(4), Reason = "t"
        }));
        Assert.Contains("最多只能提前", ex.Message);
    }

    [Fact]
    public async Task 请假_开始时间在3个月以内_提交正常通过()
    {
        var (uid, _) = SeedNightWorldForFutureCheck(DateOnly.FromDateTime(DateTime.Today));   // 需要有审批人才能提交成功
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Now.AddMonths(2);
        var request = await svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.PersonalLeave,
            LeaveStartTime = start, LeaveEndTime = start.AddHours(4), Reason = "t"
        });
        Assert.NotNull(request);
    }

    [Fact]
    public async Task 出差_开始时间超过3个月后_提交时被拒绝()
    {
        var (uid, _) = SeedWeekWorld();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Now.AddMonths(3).AddDays(1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.BusinessTrip,
            BusinessTripStartTime = start, BusinessTripEndTime = start.AddDays(2), Reason = "t"
        }));
        Assert.Contains("最多只能提前", ex.Message);
    }

    [Fact]
    public async Task 出差_开始时间在3个月以内_提交正常通过()
    {
        var (uid, _) = SeedNightWorldForFutureCheck(DateOnly.FromDateTime(DateTime.Today));   // 需要有审批人才能提交成功
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Now.AddMonths(2);
        var request = await svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.BusinessTrip,
            BusinessTripStartTime = start, BusinessTripEndTime = start.AddDays(2), Reason = "t"
        });
        Assert.NotNull(request);
    }

    // ── 出差：下限放宽到"今天0点"，方便补提今天已经开始的出差，但不追溯到昨天（2026-09-30 用户确认）──

    [Fact]
    public async Task 出差_开始时间是昨天_提交时被拒绝()
    {
        var (uid, _) = SeedNightWorldForFutureCheck(DateOnly.FromDateTime(DateTime.Today));
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Today.AddDays(-1).AddHours(9);   // 昨天9点，不管现在几点，昨天都已经过了今天0点这条线
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.BusinessTrip,
            BusinessTripStartTime = start, BusinessTripEndTime = start.AddDays(1), Reason = "t"
        }));
        Assert.Contains("不能早于今天0点", ex.Message);
    }

    [Fact]
    public async Task 出差_开始时间是今天0点_提交正常通过()
    {
        var (uid, _) = SeedNightWorldForFutureCheck(DateOnly.FromDateTime(DateTime.Today));
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Today;   // 今天0点整，边界值，应该允许（补提今天已经开始的出差）
        var request = await svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.BusinessTrip,
            BusinessTripStartTime = start, BusinessTripEndTime = start.AddDays(1), Reason = "t"
        });
        Assert.NotNull(request);
    }

    // ── 请假/出差时长上限 ──────────────────────────────────────────────────

    [Fact]
    public async Task 请假跨度超过上限_拒绝()
    {
        var w = SeedApprovalWorld(ApprovalLevelType.Level1, withGroupApprovers: false);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Now.AddHours(1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(w.applicant, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.AnnualLeave,
            LeaveStartTime = start, LeaveEndTime = new DateTime(9999, 12, 31),   // 提交"结束时间=9999 年"的假单
            Reason = "x"
        }));
        Assert.Contains("跨度", ex.Message);
    }

    [Fact]
    public async Task 出差跨度超过上限_拒绝()
    {
        var w = SeedApprovalWorld(ApprovalLevelType.Level1, withGroupApprovers: false);
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Now.AddHours(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(w.applicant, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.BusinessTrip, BusinessTripStartTime = start,
            BusinessTripEndTime = start.AddDays(ApprovalService.MaxLeaveOrTripSpanDays + 1), Reason = "x"
        }));
    }
}
