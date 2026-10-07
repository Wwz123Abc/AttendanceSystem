using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Pages.Approval;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NPOI.XSSF.UserModel;

namespace AttendanceSystem.Tests;

/// <summary>
/// 总审批记录页（Pages/Approval/ApprovalRecords）：2 个月窗口的日期收口、按管理范围/关键字/类型/状态查询、
/// 列表和导出共用的文字描述、导出的 Excel 内容。
/// </summary>
public class ApprovalRecordsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());

    public ApprovalRecordsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    // ── 日期窗口 ────────────────────────────────────────────────────────

    private static readonly DateOnly Today = new(2026, 9, 28);

    [Fact]
    public void 窗口起点_是今天往前推两个月()
        => Assert.Equal(new DateOnly(2026, 7, 28), ApprovalRecordsModel.GetWindowStart(Today));

    [Theory]
    [InlineData(null, null, "2026-07-28", "2026-09-28")]            // 什么都不传：整个 2 个月窗口
    [InlineData("2020-01-01", null, "2026-07-28", "2026-09-28")]    // 手改地址栏翻更早的：收进窗口
    [InlineData("2026-09-01", "2026-09-10", "2026-09-01", "2026-09-10")]   // 正常缩小范围：原样
    [InlineData("2026-09-01", "2030-01-01", "2026-09-01", "2026-09-28")]   // 终点在未来：收到今天
    [InlineData("2026-09-20", "2026-09-05", "2026-09-20", "2026-09-20")]   // 终点早于起点：按起点
    [InlineData("2026-12-01", null, "2026-09-28", "2026-09-28")]    // 起点在未来：收到今天
    public void 日期范围_都被收进两个月窗口内(string? start, string? end, string expStart, string expEnd)
    {
        var (s, e) = ApprovalRecordsModel.ClampRange(
            start is null ? null : DateOnly.Parse(start), end is null ? null : DateOnly.Parse(end), Today);
        Assert.Equal(DateOnly.Parse(expStart), s);
        Assert.Equal(DateOnly.Parse(expEnd), e);
    }

    // ── 查询：管理范围 / 关键字 / 类型 / 状态 / 日期 ─────────────────────

    private (int deptA, int deptB, int userA, int userB, int approver) Seed()
    {
        using var db = CreateContext();
        var a = new Department { DeptName = "A分公司" }; var b = new Department { DeptName = "B分公司" };
        db.Departments.AddRange(a, b);
        db.SaveChanges();
        var ua = new User { EmployeeNo = "A001", RealName = "甲员工", PasswordHash = "x", IsActive = true, DepartmentId = a.Id };
        var ub = new User { EmployeeNo = "B001", RealName = "乙员工", PasswordHash = "x", IsActive = true, DepartmentId = b.Id };
        var boss = new User { EmployeeNo = "M001", RealName = "审批人", PasswordHash = "x", IsActive = true, DepartmentId = a.Id };
        db.Users.AddRange(ua, ub, boss);
        db.SaveChanges();

        ApprovalRequest R(string no, User u, ApprovalType t, ApprovalStatus st, DateTime submitted) => new()
        {
            RequestNo = no, ApplicantUserId = u.Id, ApprovalType = t, ApprovalStatus = st, SubmittedAt = submitted, Reason = "理由" + no
        };
        var leave = R("QJ-A", ua, ApprovalType.Leave, ApprovalStatus.Approved, new DateTime(2026, 9, 20, 10, 0, 0));
        leave.LeaveType = LeaveType.PersonalLeave; leave.LeaveStartTime = new DateTime(2026, 9, 22, 8, 30, 0);
        leave.LeaveEndTime = new DateTime(2026, 9, 22, 17, 30, 0); leave.LeaveDurationHours = 8;
        var ot = R("JB-A", ua, ApprovalType.Overtime, ApprovalStatus.InProgress, new DateTime(2026, 9, 26, 22, 1, 0));
        ot.OvertimeStartTime = new DateTime(2026, 9, 26, 8, 30, 0); ot.OvertimeEndTime = new DateTime(2026, 9, 26, 22, 0, 0); ot.OvertimeDurationHours = 12;
        var bk = R("BK-B", ub, ApprovalType.PunchReplenishment, ApprovalStatus.Rejected, new DateTime(2026, 9, 27, 9, 0, 0));
        bk.PunchDate = new DateOnly(2026, 9, 25); bk.PunchType = PunchType.ClockOut; bk.PunchTime = new TimeOnly(17, 30);
        var old = R("QJ-OLD", ua, ApprovalType.Leave, ApprovalStatus.Approved, new DateTime(2026, 6, 1, 10, 0, 0));   // 窗口之外
        db.ApprovalRequests.AddRange(leave, ot, bk, old);
        db.SaveChanges();
        db.ApprovalSteps.AddRange(
            new ApprovalStep { ApprovalRequestId = leave.Id, ApproverUserId = boss.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Approved, Comment = "同意", HandledAt = new DateTime(2026, 9, 21, 9, 0, 0) },
            new ApprovalStep { ApprovalRequestId = ot.Id, ApproverUserId = boss.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Approved, HandledAt = new DateTime(2026, 9, 27, 8, 0, 0) },
            new ApprovalStep { ApprovalRequestId = ot.Id, ApproverUserId = boss.Id, StepOrder = 2, ApprovalStatus = ApprovalStatus.Pending });
        db.SaveChanges();
        return (a.Id, b.Id, ua.Id, ub.Id, boss.Id);
    }

    private static ApprovalQueryDto Q() => new()
    {
        StartDate = new DateTime(2026, 7, 28), EndDate = new DateTime(2026, 9, 28, 23, 59, 59), PageSize = 100
    };

    private ApprovalService Svc(AttendanceDbContext db) => new(db, new FakeAttendanceService(), AppOptions);

    [Fact]
    public async Task 查询_按管理范围收窄_分公司账号只看到自己部门员工的申请()
    {
        var (deptA, deptB, _, _, _) = Seed();
        using var db = CreateContext();
        var (all, totalAll) = await Svc(db).QueryApprovalsAsync(Q(), deptIds: null);                 // 总部：全部（窗口内）
        Assert.Equal(3, totalAll);
        Assert.DoesNotContain(all, r => r.RequestNo == "QJ-OLD");                                    // 窗口外的老单子不在里面

        var (onlyA, totalA) = await Svc(db).QueryApprovalsAsync(Q(), deptIds: [deptA]);
        Assert.Equal(["JB-A", "QJ-A"], onlyA.Select(r => r.RequestNo).Order().ToList());
        Assert.Equal(2, totalA);

        var (onlyB, _) = await Svc(db).QueryApprovalsAsync(Q(), deptIds: [deptB]);
        Assert.Equal(["BK-B"], onlyB.Select(r => r.RequestNo).ToList());
    }

    [Fact]
    public async Task 查询_关键字类型状态日期都能过滤()
    {
        Seed();
        using var db = CreateContext();
        var svc = Svc(db);

        var q1 = Q(); q1.Keyword = "乙员工";
        Assert.Equal(["BK-B"], (await svc.QueryApprovalsAsync(q1)).Items.Select(r => r.RequestNo).ToList());      // 姓名
        var q2 = Q(); q2.Keyword = "A001";
        Assert.Equal(2, (await svc.QueryApprovalsAsync(q2)).Total);                                               // 工号
        var q3 = Q(); q3.Keyword = "JB-A";
        Assert.Equal(["JB-A"], (await svc.QueryApprovalsAsync(q3)).Items.Select(r => r.RequestNo).ToList());      // 单号
        var q4 = Q(); q4.ApprovalType = ApprovalType.Leave;
        Assert.Equal(["QJ-A"], (await svc.QueryApprovalsAsync(q4)).Items.Select(r => r.RequestNo).ToList());      // 类型
        var q5 = Q(); q5.ApprovalStatus = ApprovalStatus.Rejected;
        Assert.Equal(["BK-B"], (await svc.QueryApprovalsAsync(q5)).Items.Select(r => r.RequestNo).ToList());      // 状态
        var q6 = Q(); q6.StartDate = new DateTime(2026, 9, 26); q6.EndDate = new DateTime(2026, 9, 26, 23, 59, 59);
        Assert.Equal(["JB-A"], (await svc.QueryApprovalsAsync(q6)).Items.Select(r => r.RequestNo).ToList());      // 提交日期
    }

    [Fact]
    public async Task 查询_按提交时间倒序_并带出各级审批()
    {
        Seed();
        using var db = CreateContext();
        var (items, _) = await Svc(db).QueryApprovalsAsync(Q());
        Assert.Equal(["BK-B", "JB-A", "QJ-A"], items.Select(r => r.RequestNo).ToList());   // 9/27、9/26、9/20
        Assert.Equal(2, items.Single(r => r.RequestNo == "JB-A").Steps.Count);
    }

    // ── 列表/导出共用的文字 ─────────────────────────────────────────────

    [Fact]
    public async Task 申请内容_时长_审批流程的文字描述()
    {
        Seed();
        using var db = CreateContext();
        var (items, _) = await Svc(db).QueryApprovalsAsync(Q());

        var leave = items.Single(r => r.RequestNo == "QJ-A");
        Assert.Equal("事假：2026-09-22 08:30 ~ 2026-09-22 17:30", leave.ContentText);
        Assert.Equal("8 小时", leave.DurationText);
        Assert.Equal("第1级 审批人：已通过（2026-09-21 09:00）「同意」", leave.StepsText);

        var ot = items.Single(r => r.RequestNo == "JB-A");
        Assert.Equal("12 小时", ot.DurationText);
        Assert.Equal("第1级 审批人：已通过（2026-09-27 08:00）\n第2级 审批人：待审批", ot.StepsText);   // 每级一行，没处理的不带时间

        var bk = items.Single(r => r.RequestNo == "BK-B");
        Assert.Equal("补下班卡 2026-09-25 17:30", bk.ContentText);
        Assert.Equal("", bk.DurationText);                                // 补卡没有时长
    }

    // ── 导出的 Excel ────────────────────────────────────────────────────

    [Fact]
    public async Task 导出Excel_表头_行数_内容都正确()
    {
        Seed();
        using var db = CreateContext();
        var (items, _) = await Svc(db).QueryApprovalsAsync(Q());
        var bytes = ExcelExportHelper.ExportApprovalRecords(items, new DateOnly(2026, 7, 28), new DateOnly(2026, 9, 28));

        using var wb = new XSSFWorkbook(new MemoryStream(bytes));
        var sheet = wb.GetSheetAt(0);
        Assert.Equal("总审批记录", sheet.SheetName);
        Assert.Contains("共 3 条", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(["申请单号", "类型", "申请人工号", "申请人", "部门", "申请内容", "时长", "申请理由", "提交时间", "当前状态", "审批流程 / 意见", "最后处理时间"],
            Enumerable.Range(0, 12).Select(i => sheet.GetRow(1).GetCell(i).StringCellValue).ToList());
        Assert.Equal(1 + 1 + 3, sheet.LastRowNum + 1);                    // 标题 + 表头 + 3 条数据

        var first = sheet.GetRow(2);                                      // 按提交时间倒序：BK-B 在最前
        Assert.Equal("BK-B", first.GetCell(0).StringCellValue);
        Assert.Equal("补卡", first.GetCell(1).StringCellValue);
        Assert.Equal("乙员工", first.GetCell(3).StringCellValue);
        Assert.Equal("B分公司", first.GetCell(4).StringCellValue);
        Assert.Equal("已驳回", first.GetCell(9).StringCellValue);

        var ot = sheet.GetRow(3);
        Assert.Equal("JB-A", ot.GetCell(0).StringCellValue);
        Assert.Contains("\n", ot.GetCell(10).StringCellValue);            // 两级审批：一格里两行
        Assert.Equal("2026-09-27 08:00", ot.GetCell(11).StringCellValue); // 最后处理时间 = 已处理的最后一级
    }

    [Fact]
    public void 导出Excel_没有数据时也能正常生成()
    {
        var bytes = ExcelExportHelper.ExportApprovalRecords([], new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 28));
        using var wb = new XSSFWorkbook(new MemoryStream(bytes));
        Assert.Equal(1, wb.GetSheetAt(0).LastRowNum);                     // 只有标题和表头
    }
}
