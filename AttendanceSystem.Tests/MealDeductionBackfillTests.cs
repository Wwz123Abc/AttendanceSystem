using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-09-29 用户确认"现在就重算 9/26 起的记录"：饭点规则从旧口径（超过 6/9 小时扣整段）
/// 改成新口径（固定 4 个时段按重叠扣减）之后，本工资周期（9/26 起）已经落库、但还是按旧口径
/// 算好的正班工时/加班时长/请假时长，要一次性回填成新口径的结果。这里测
/// AttendanceService.RecalcMealDeductionBackfillAsync：加班/请假按"天"整体重算、
/// fromDate 之前的天数不动、半天假+真实打卡的工时封顶用的是回填后的请假小时数、幂等、dryRun 不落库。
/// </summary>
public class MealDeductionBackfillTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());
    private static readonly DateOnly FromDate = new(2026, 9, 26);

    public MealDeductionBackfillTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    private AttendanceService Svc(AttendanceDbContext db) => new(db, AppOptions, NullLogger<AttendanceService>.Instance);

    private int SeedUser(AttendanceDbContext db, string no)
    {
        var u = new User { EmployeeNo = no, RealName = no, PasswordHash = "x", IsActive = true, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(u);
        db.SaveChanges();
        return u.Id;
    }

    [Fact]
    public async Task 加班_单张申请_按新公式重新算出时长_并同步到考勤记录()
    {
        int uid;
        using (var db = CreateContext())
        {
            uid = SeedUser(db, "OT1");
            var start = FromDate.ToDateTime(new TimeOnly(9, 0));
            var end   = FromDate.ToDateTime(new TimeOnly(15, 0));   // 6 小时，压中 12:00-13:00
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "JB-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime,
                ApprovalStatus = ApprovalStatus.Approved, OvertimeStartTime = start, OvertimeEndTime = end,
                OvertimeDurationHours = 6, Reason = "t"   // 旧口径：正好 6 小时不算"超过"，不扣
            });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = FromDate, OvertimeHours = 6 });
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var result = await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: false);
            Assert.Equal(1, result.OvertimeRequestsAdjusted);
            Assert.Equal(1, result.OvertimeDaysChanged);
        }

        using var check = CreateContext();
        Assert.Equal(5m, (await check.ApprovalRequests.SingleAsync()).OvertimeDurationHours);   // 6h 扣掉 12:00-13:00 那 1 小时
        Assert.Equal(5m, (await check.AttendanceRecords.SingleAsync()).OvertimeHours);
    }

    [Fact]
    public async Task 加班_同一天两张已批准申请_按天求和_不是简单累加旧值()
    {
        int uid;
        using (var db = CreateContext())
        {
            uid = SeedUser(db, "OT2");
            db.ApprovalRequests.AddRange(
                new ApprovalRequest
                {
                    RequestNo = "JB-A", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Approved,
                    OvertimeStartTime = FromDate.ToDateTime(new TimeOnly(9, 0)), OvertimeEndTime = FromDate.ToDateTime(new TimeOnly(11, 0)),
                    OvertimeDurationHours = 3, Reason = "t"   // 没压中任何饭点时段，新口径应为 2（旧值 3 是错的，模拟旧口径存量）
                },
                new ApprovalRequest
                {
                    RequestNo = "JB-B", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Approved,
                    OvertimeStartTime = FromDate.ToDateTime(new TimeOnly(12, 30)), OvertimeEndTime = FromDate.ToDateTime(new TimeOnly(14, 0)),
                    OvertimeDurationHours = 2, Reason = "t"   // 1.5 小时，压中 12:30-13:00 半小时，新口径应为 1.0
                });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = FromDate, OvertimeHours = 5 });   // 旧值 3+2
            db.SaveChanges();
        }

        using (var db = CreateContext())
            await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: false);

        using var check = CreateContext();
        var reqs = await check.ApprovalRequests.OrderBy(a => a.RequestNo).ToListAsync();
        Assert.Equal(2m, reqs[0].OvertimeDurationHours);
        Assert.Equal(1m, reqs[1].OvertimeDurationHours);
        Assert.Equal(3m, (await check.AttendanceRecords.SingleAsync()).OvertimeHours);   // 2+1，不是旧的 5
    }

    [Fact]
    public async Task 请假_单日压中饭点时段_按新公式重新算()
    {
        // 事假遇到"没排班+周六周日"会被当成非工作日直接跳过（IsNonWorkday，跟正常业务口径一致），
        // FromDate（2026-09-26）恰好是周六，这里用窗口内的一个工作日（周一）来测，避免跟那条规则混在一起
        var day = FromDate.AddDays(2);   // 2026-09-28，周一
        int uid;
        using (var db = CreateContext())
        {
            uid = SeedUser(db, "LV1");
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "QJ-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.PersonalLeave,
                ApprovalStatus = ApprovalStatus.Approved,
                LeaveStartTime = day.ToDateTime(new TimeOnly(9, 0)), LeaveEndTime = day.ToDateTime(new TimeOnly(13, 0)),
                Reason = "t"
            });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = day, AttendanceStatus = AttendanceStatus.OnLeave, LeaveHours = 4 });
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var result = await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: false);
            Assert.Equal(1, result.LeaveDaysChanged);
        }

        using var check = CreateContext();
        Assert.Equal(3m, (await check.AttendanceRecords.SingleAsync()).LeaveHours);   // 4h 扣掉 12:00-13:00 那 1 小时
    }

    [Fact]
    public async Task 请假_跨天_只重算fromDate起的部分_之前的天数不动()
    {
        int uid;
        var d0 = FromDate.AddDays(-1);   // 早于回填窗口，不应该被动
        var d1 = FromDate;               // 回填窗口内，恰好是周六——用婚假（按自然日算，不受"非工作日跳过"影响）
                                          // 避免跟 IsNonWorkday 那条规则的判断混在一起，这条测试只关心"窗口边界"
        using (var db = CreateContext())
        {
            uid = SeedUser(db, "LV2");
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "QJ-2", ApplicantUserId = uid, ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.MarriageLeave,
                ApprovalStatus = ApprovalStatus.Approved,
                LeaveStartTime = d0.ToDateTime(new TimeOnly(13, 0)), LeaveEndTime = d1.ToDateTime(new TimeOnly(13, 0)),
                Reason = "t"
            });
            // d0 当天存的是"旧口径算出来的、本次不该被动"的占位值；d1 当天存一个明显错误的旧值等着被修正
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = d0, AttendanceStatus = AttendanceStatus.OnLeave, LeaveHours = 999 });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = d1, AttendanceStatus = AttendanceStatus.OnLeave, LeaveHours = 999 });
            db.SaveChanges();
        }

        using (var db = CreateContext())
            await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: false);

        using var check = CreateContext();
        var recs = await check.AttendanceRecords.ToDictionaryAsync(r => r.WorkDate);
        Assert.Equal(999m, recs[d0].LeaveHours);   // 窗口之前：完全不动
        // 窗口内：d1 当天 00:00~13:00（没排班，按自然日切），压中 00:00-01:00(1h)+05:30-06:00(0.5h)+12:00-13:00(1h)
        // 共 2.5h 扣减，13h-2.5h=10.5h，但没排班时按公司默认标准工时（8h）封顶，所以是 8
        Assert.Equal(8m, recs[d1].LeaveHours);
    }

    [Fact]
    public async Task 半天假加真实打卡_工时重算用的是回填后的请假小时数_不是回填前的旧值()
    {
        var day = FromDate.AddDays(2);   // 2026-09-28，周一——避开 FromDate 恰好是周六、没排班会被当休息日清零工时的规则
        int uid;
        using (var db = CreateContext())
        {
            uid = SeedUser(db, "LV3");
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "QJ-3", ApplicantUserId = uid, ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.PersonalLeave,
                ApprovalStatus = ApprovalStatus.Approved,
                LeaveStartTime = day.ToDateTime(new TimeOnly(13, 0)), LeaveEndTime = day.ToDateTime(new TimeOnly(17, 30)),
                Reason = "t"
            });
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = day, AttendanceStatus = AttendanceStatus.OnLeave,
                LeaveHours = 5,   // 旧值（错误，模拟旧口径存量）
                ClockInTime = day.ToDateTime(new TimeOnly(8, 30)), ClockOutTime = day.ToDateTime(new TimeOnly(12, 0)),
                ActualWorkHours = 3   // 旧值：min(3.5, 8-5)=3
            });
            db.SaveChanges();
        }

        using (var db = CreateContext())
            await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: false);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync();
        Assert.Equal(4.5m, rec.LeaveHours);      // 13:00-17:30，没压中任何饭点时段，4.5h 不扣
        Assert.Equal(3.5m, rec.ActualWorkHours); // min(3.5, 8-4.5=3.5)=3.5，不是用旧请假值 5 算出来的 3
    }

    [Fact]
    public async Task dryRun为true时不落库()
    {
        int uid;
        using (var db = CreateContext())
        {
            uid = SeedUser(db, "DRY1");
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "JB-D", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Approved,
                OvertimeStartTime = FromDate.ToDateTime(new TimeOnly(9, 0)), OvertimeEndTime = FromDate.ToDateTime(new TimeOnly(15, 0)),
                OvertimeDurationHours = 6, Reason = "t"
            });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = FromDate, OvertimeHours = 6 });
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var result = await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: true);
            Assert.Equal(1, result.OvertimeRequestsAdjusted);   // 计算出来的结果照常返回
        }

        using var check = CreateContext();
        Assert.Equal(6m, (await check.ApprovalRequests.SingleAsync()).OvertimeDurationHours);   // 但没有真的写进数据库
        Assert.Equal(6m, (await check.AttendanceRecords.SingleAsync()).OvertimeHours);
    }

    [Fact]
    public async Task 幂等_跑两遍结果一样()
    {
        int uid;
        using (var db = CreateContext())
        {
            uid = SeedUser(db, "IDEM1");
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "JB-I", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Approved,
                OvertimeStartTime = FromDate.ToDateTime(new TimeOnly(9, 0)), OvertimeEndTime = FromDate.ToDateTime(new TimeOnly(15, 0)),
                OvertimeDurationHours = 6, Reason = "t"
            });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = FromDate, OvertimeHours = 6 });
            db.SaveChanges();
        }

        using (var db = CreateContext())
            await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: false);

        using (var db = CreateContext())
        {
            var second = await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: false);
            Assert.Equal(0, second.OvertimeRequestsAdjusted);   // 第二遍已经是新值了，不会再变
            Assert.Equal(0, second.OvertimeDaysChanged);
        }

        using var check = CreateContext();
        Assert.Equal(5m, (await check.AttendanceRecords.SingleAsync()).OvertimeHours);
    }

    // ── 第 13 轮审查发现的两个问题 ────────────────────────────────────────────

    [Fact]
    public async Task 只打了上班卡的下午半天假_回填后工时按回填后的请假小时数重新封顶()
    {
        // 2026-09-29 第 13 轮审查发现：第 ③ 步（正班工时）以前只处理有上下班卡的记录，"上午上班、
        // 下午请假、没打下班卡"这种半天假会被漏掉——第 ② 步已经把 LeaveHours 改成新值，
        // 工时却还停在按旧 LeaveHours 封顶的旧值，少算了工时。
        var day = FromDate.AddDays(2);   // 2026-09-28，周一
        int uid;
        using (var db = CreateContext())
        {
            uid = SeedUser(db, "LV4");
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "QJ-4", ApplicantUserId = uid, ApprovalType = ApprovalType.Leave, LeaveType = LeaveType.PersonalLeave,
                ApprovalStatus = ApprovalStatus.Approved,
                LeaveStartTime = day.ToDateTime(new TimeOnly(12, 0)), LeaveEndTime = day.ToDateTime(new TimeOnly(17, 30)),
                Reason = "t"
            });
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = day, AttendanceStatus = AttendanceStatus.OnLeave,
                LeaveHours = 5.5m,          // 旧值（错误，模拟旧口径存量）
                ClockInTime = day.ToDateTime(new TimeOnly(8, 25)), ClockOutTime = null,   // 只打了上班卡
                ActualWorkHours = 2.5m      // 旧值：min(3.5, 8-5.5)=2.5
            });
            db.SaveChanges();
        }

        using (var db = CreateContext())
            await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: false);

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync();
        Assert.Equal(4.5m, rec.LeaveHours);       // 12:00-17:30，压中 12:00-13:00 那 1 小时
        Assert.Equal(3.5m, rec.ActualWorkHours);  // min(ComputeWorkHours(8:25,12:00)=3.5, 8-4.5=3.5)=3.5，不是旧值 2.5
    }

    [Fact]
    public async Task 回填后月度汇总会重新生成_我的记录能看到新值()
    {
        // 2026-09-29 第 13 轮审查发现：第 ③ 步重算正班工时时没有更新 UpdatedAt，"我的记录/我的日历"
        // 靠比较记录的修改时间判断要不要刷新月度汇总（EnsureMonthlySummaryFreshAsync），不更新
        // 时间戳的话页面永远读到回填前的旧汇总。现在回填完会顺手把涉及的月份整月重算一次。
        var day = FromDate.AddDays(2);   // 2026-09-28，周一
        int uid;
        using (var db = CreateContext())
        {
            uid = SeedUser(db, "MS1");
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = day, AttendanceStatus = AttendanceStatus.Normal,
                ClockInTime = day.ToDateTime(new TimeOnly(8, 0)), ClockOutTime = day.ToDateTime(new TimeOnly(14, 0)),
                ActualWorkHours = 6m   // 旧值（错误，模拟旧口径存量）；新口径应为 5（压中 12:00-13:00 那 1 小时）
            });
            db.MonthlyAttendanceSummaries.Add(new MonthlyAttendanceSummary
            {
                UserId = uid, Year = day.Year, Month = day.Month, TotalWorkHours = 6m,
                UpdatedAt = DateTime.Now.AddDays(-10)   // 明显陈旧的汇总，模拟回填前已经生成过一次
            });
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var result = await Svc(db).RecalcMealDeductionBackfillAsync(FromDate, dryRun: false);
            Assert.Equal(1, result.WorkHoursRecordsChanged);
        }

        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync();
        Assert.Equal(5m, rec.ActualWorkHours);
        Assert.True(rec.UpdatedAt > DateTime.Now.AddMinutes(-1));   // 时间戳被刷新了

        var summary = await check.MonthlyAttendanceSummaries.SingleAsync(s => s.UserId == uid && s.Year == day.Year && s.Month == day.Month);
        Assert.Equal(5m, summary.TotalWorkHours);   // 汇总跟着重新生成，不再是回填前的旧值 6
    }
}
