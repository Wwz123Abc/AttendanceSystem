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
/// 加班与工时规则：旷工日补一边卡、审批单号、休息日不计正班、加班按饭点扣时、加班不能与班次重叠。
/// </summary>
public class OvertimeAndWorkHoursRuleTests : SqliteTestBase
{
    // ── ③ 只补一边卡的旷工 ─────────────────────────────────────────────

    [Theory]
    [InlineData(true)]    // 只补上班卡
    [InlineData(false)]   // 只补下班卡
    public async Task 手动补卡_旷工日只补一边_状态改成未打卡_不再挂旷工(bool onlyClockIn)
    {
        var (uid, _) = SeedWeekWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Mon, AttendanceStatus = AttendanceStatus.Absent });
            db.SaveChanges();
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var t = Mon.ToDateTime(new TimeOnly(onlyClockIn ? 8 : 17, 30));
            await svc.AdminAdjustPunchAsync(uid, Mon, onlyClockIn ? t : null, onlyClockIn ? null : t, null, "管理员");
        }
        using var check = CreateContext();
        Assert.Equal(AttendanceStatus.NotPunched, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon)).AttendanceStatus);
    }

    // ── ④ 审批单号 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 审批单号_当天前面的单被删过_也不会撞号()
    {
        var (uid, _) = SeedWeekWorld();
        var head = "QJ" + DateTime.Now.ToString("yyyyMMdd");
        using (var db = CreateContext())
        {
            // 当天只剩 0002（0001 已被删除）：按"条数+1"算出来是 0002，一直撞号
            db.ApprovalRequests.Add(new ApprovalRequest { RequestNo = head + "0002", ApplicantUserId = uid, ApprovalType = ApprovalType.Leave, Reason = "t" });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var svc = new ApprovalService(db2, new FakeAttendanceService(), AppOptions);
        Assert.Equal(head + "0003", await svc.GenerateRequestNoAsync(ApprovalType.Leave));
        Assert.Equal("BK" + DateTime.Now.ToString("yyyyMMdd") + "0001", await svc.GenerateRequestNoAsync(ApprovalType.PunchReplenishment));   // 别的类型各数各的
    }

    [Fact]
    public async Task 加班申请_单次超过24小时_提交时被拒绝()
    {
        var (uid, _) = SeedWeekWorld();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Today.AddHours(8);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Overtime, Reason = "t", OvertimeStartTime = start, OvertimeEndTime = start.AddHours(25)
        }));
        Assert.Contains("24", ex.Message);
    }

    // ── ⑩ 休息日不计正班工时，批了加班的只算加班（2026-09-28 用户确认）────────

    [Theory]
    [InlineData(13.5)]    // 休息日全天加班：正班 0，只有加班 13.5
    [InlineData(0)]       // 休息日没批加班：本来就是 0
    public async Task 手动补卡_休息日不计正班工时_有加班也一样(double otHours)
    {
        var (uid, _) = SeedWeekWorld();   // 周五~周一都排了白班，班次配置的每周休息日是周六周日
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Sat, OvertimeHours = (decimal)otHours });
            db.SaveChanges();
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.AdminAdjustPunchAsync(uid, Sat, Sat.ToDateTime(new TimeOnly(8, 24)), Sat.ToDateTime(new TimeOnly(22, 2)), null, "管理员");
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Sat);
        Assert.Equal(0m, rec.ActualWorkHours);                  // 以前有加班时这里是 8：正班 8 + 加班 13.5 重复计算
        Assert.Equal((decimal)otHours, rec.OvertimeHours);      // 加班时长以审批单为准，不受影响
    }

    [Fact]
    public async Task 手动补卡_工作日照常计正班工时_加班另算()
    {
        var (uid, _) = SeedWeekWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Mon, OvertimeHours = 4m });
            db.SaveChanges();
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.AdminAdjustPunchAsync(uid, Mon, Mon.ToDateTime(new TimeOnly(8, 25)), Mon.ToDateTime(new TimeOnly(22, 0)), null, "管理员");
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(8m, rec.ActualWorkHours);     // 工作日：正班封顶在应下班时间，8 小时
        Assert.Equal(4m, rec.OvertimeHours);
    }

    [Fact]
    public async Task 考勤机同步_休息日有加班_正班工时也是0_只有加班()
    {
        var (uid, _) = SeedDayMidWorld();
        using (var db = CreateContext())
        {
            // 把排班挪到周六（班次配置的每周休息日），并提前批好加班
            var assign = db.ShiftAssignments.Single(a => a.UserId == uid);
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = uid, WorkDate = Sat, ShiftScheduleId = assign.ShiftScheduleId });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Sat, OvertimeHours = 13.5m });
            db.SaveChanges();
        }
        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
            await svc.ProcessAttLogAsync("SNM", [
                new ZKAttLogRow("M1", Sat.ToDateTime(new TimeOnly(8, 24)), 0, 15),
                new ZKAttLogRow("M1", Sat.ToDateTime(new TimeOnly(22, 2)), 0, 15)]);
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Sat);
        Assert.NotNull(rec.ClockOutTime);
        Assert.Equal(0m, rec.ActualWorkHours);
        Assert.Equal(13.5m, rec.OvertimeHours);
    }

    // ── ⑪ 加班时长按申请的时间段扣饭点：压到公司统一的午间/晚餐/宵夜/早餐时段就扣对应重叠时长
    // （2026-09-28 用户确认扣饭点，2026-09-29 改成按实际重叠扣减，不再看"是否超过 6/9 小时"）────

    [Theory]
    [InlineData(18, 0, 22, 0, 4)]       // 4 小时：不扣
    [InlineData(8, 30, 17, 30, 8)]      // 9 小时：超过 6 小时扣午休 60 分钟；正好 9 小时不算"超过 9 小时"，不扣晚餐
    [InlineData(8, 30, 22, 0, 12)]      // 13.5 小时（陈林发 9/26 那张）：扣 60 + 30 分钟 = 12 小时
    [InlineData(8, 0, 20, 0, 10.5)]     // 12 小时 → 10.5
    [InlineData(9, 0, 15, 0, 5)]        // 6 小时，但压中了 12:00-13:00 这段午间时段 → 扣 1 小时，剩 5 小时
                                         // （2026-09-29 起不再看"是否超过 6/9 小时"，只看跟固定饭点时段有没有重叠）
    public async Task 加班审批通过回写_申请时长超过6小时9小时按规则扣饭点(int sh, int sm, int eh, int em, double expected)
    {
        var (uid, _) = SeedWeekWorld();
        var start = Mon.ToDateTime(new TimeOnly(sh, sm));
        var end   = Mon.ToDateTime(new TimeOnly(eh, em));
        var raw   = (decimal)(end - start).TotalHours;
        var id = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = $"JB-T-{sh}{sm}-{eh}{em}", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
            OvertimeStartTime = start, OvertimeEndTime = end, OvertimeDurationHours = raw,   // 单子上存的是不扣饭点的总长度（老单子/未回写时的样子）
            Reason = "t"
        });

        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal((decimal)expected, rec.OvertimeHours);
        Assert.Equal((decimal)expected, (await check.ApprovalRequests.SingleAsync(a => a.Id == id)).OvertimeDurationHours);   // 单子上的时长同步成实际记入的数
    }

    [Fact]
    public async Task 加班申请_不足半小时_提交时被拒绝()
    {
        var (uid, _) = SeedWeekWorld();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var start = DateTime.Today.AddHours(18);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
        {
            ApprovalType = ApprovalType.Overtime, Reason = "t", OvertimeStartTime = start, OvertimeEndTime = start.AddMinutes(20)
        }));
        Assert.Contains("0.5", ex.Message);
    }

    // ── ⑬ 工作日加班时间不能和当天班次的上下班时间重叠（2026-09-28 用户确认）────────────

    [Theory]
    [InlineData(8, 30, 17, 30)]     // 整个班次都填成加班（线上 110 张就是这样）
    [InlineData(8, 30, 19, 30)]     // 上班时间开始，一直加班到晚上
    [InlineData(9, 0, 12, 0)]       // 完全落在班次里面
    [InlineData(7, 0, 9, 0)]        // 早上提前上班，但压到了班次开始之后（08:30~09:00）
    [InlineData(16, 0, 19, 0)]      // 下班前就开始填（16:00~17:30 重叠）
    public async Task 工作日加班_和班次上下班时间重叠_提交被拒绝(int sh, int sm, int eh, int em)
    {
        var uid = SeedOvertimeWorld(restDays: "");   // 没有每周休息日：今天肯定是工作日
        var ex = await TrySubmitOvertimeAsync(uid, new TimeOnly(sh, sm), new TimeOnly(eh, em));
        var ioe = Assert.IsType<BusinessException>(ex);
        Assert.StartsWith("加班时间不能和上班时间重叠，请只填下班后（或上班前）的加班时段", ioe.Message);
        Assert.Contains("08:30–17:30", ioe.Message);            // 提示里带上当天的班次时间
    }

    [Theory]
    [InlineData(17, 30, 21, 0)]     // 正好从下班时间开始：不重叠
    [InlineData(18, 0, 22, 0)]      // 下班后
    [InlineData(5, 0, 8, 30)]       // 上班前，正好到上班时间：不重叠
    public async Task 工作日加班_只填下班后或上班前_不受影响(int sh, int sm, int eh, int em)
    {
        var uid = SeedOvertimeWorld(restDays: "");
        Assert.Null(await TrySubmitOvertimeAsync(uid, new TimeOnly(sh, sm), new TimeOnly(eh, em)));
    }

    [Fact]
    public async Task 休息日加班_整天都可以填_不受限制()
    {
        var todayDow = ((int)DateTime.Today.DayOfWeek).ToString();
        var uid = SeedOvertimeWorld(restDays: todayDow);            // 今天是这个班次的每周休息日
        Assert.Null(await TrySubmitOvertimeAsync(uid, new TimeOnly(8, 30), new TimeOnly(20, 0)));
    }

    [Fact]
    public async Task 当天没排班_不判断重叠()
    {
        var uid = SeedOvertimeWorld(restDays: "");
        using (var db = CreateContext())
        {
            db.ShiftAssignments.RemoveRange(db.ShiftAssignments.Where(a => a.UserId == uid));
            db.SaveChanges();
        }
        Assert.Null(await TrySubmitOvertimeAsync(uid, new TimeOnly(8, 30), new TimeOnly(17, 30)));
    }
}
