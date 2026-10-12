using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>总审批记录的"审批进度"：没审批完的申请 + 每个审批人手上压了几张（现在轮到的 / 排在后续级别的）。</summary>
public class ApprovalProgressTests : SqliteTestBase
{
    private static ApprovalRequest Req(string no, int applicantId, ApprovalStatus status, DateTime submitted) => new()
    {
        RequestNo = no, ApplicantUserId = applicantId, ApprovalType = ApprovalType.Overtime, ApprovalStatus = status,
        OvertimeStartTime = submitted.Date.AddHours(18), OvertimeEndTime = submitted.Date.AddHours(20), OvertimeDurationHours = 2,
        SubmittedAt = submitted, UpdatedAt = submitted
    };

    private static ApprovalStep Step(int reqId, int approverId, int order, ApprovalStatus s) =>
        new() { ApprovalRequestId = reqId, ApproverUserId = approverId, StepOrder = order, ApprovalStatus = s };

    private (int a, int b, int deptX, int deptY) Seed()
    {
        using var db = CreateContext();
        var dx = new Department { DeptName = "X部" }; var dy = new Department { DeptName = "Y部" };
        db.Departments.AddRange(dx, dy);
        db.SaveChanges();
        var ap = U("AP", "申请人甲"); ap.DepartmentId = dx.Id;
        var bp = U("BP", "申请人乙"); bp.DepartmentId = dy.Id;
        var a = U("A1", "审批人A"); a.Role = UserRole.TeamLeader;
        var b = U("B1", "审批人B"); b.Role = UserRole.Supervisor;
        db.Users.AddRange(ap, bp, a, b);
        db.SaveChanges();

        var now = DateTime.Now;
        // R1：二级审批，一级 A 还没审、二级 B 排在后面 → 现在轮到 A，B 是后续
        var r1 = Req("R1", ap.Id, ApprovalStatus.Pending, now.AddDays(-5));
        // R2：二级审批，A 已通过，现在轮到 B
        var r2 = Req("R2", ap.Id, ApprovalStatus.InProgress, now.AddDays(-2));
        // R3：已通过，不算未审批
        var r3 = Req("R3", ap.Id, ApprovalStatus.Approved, now.AddDays(-9));
        // R4：别的部门的申请，单级，轮到 A
        var r4 = Req("R4", bp.Id, ApprovalStatus.Pending, now.AddDays(-1));
        db.ApprovalRequests.AddRange(r1, r2, r3, r4);
        db.SaveChanges();
        db.ApprovalSteps.AddRange(
            Step(r1.Id, a.Id, 1, ApprovalStatus.Pending), Step(r1.Id, b.Id, 2, ApprovalStatus.Pending),
            Step(r2.Id, a.Id, 1, ApprovalStatus.Approved), Step(r2.Id, b.Id, 2, ApprovalStatus.Pending),
            Step(r3.Id, a.Id, 1, ApprovalStatus.Approved),
            Step(r4.Id, a.Id, 1, ApprovalStatus.Pending));
        db.SaveChanges();
        return (a.Id, b.Id, dx.Id, dy.Id);
    }

    [Fact]
    public async Task 审批进度_按现在该谁审汇总_后续级别单独计数_已通过的不算()
    {
        var (a, b, _, _) = Seed();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var p = await svc.GetApprovalProgressAsync();

        Assert.Equal(["R1", "R2", "R4"], p.Items.Select(i => i.RequestNo).OrderBy(n => n).ToList());   // R3 已通过不在里面
        var rowA = Assert.Single(p.Approvers, r => r.ApproverUserId == a);
        Assert.Equal((2, 0), (rowA.CurrentCount, rowA.LaterCount));      // R1、R4 现在轮到 A
        var rowB = Assert.Single(p.Approvers, r => r.ApproverUserId == b);
        Assert.Equal((1, 1), (rowB.CurrentCount, rowB.LaterCount));      // R2 轮到 B；R1 的二级是后续
        Assert.Equal("班组长", rowA.RoleText);
        Assert.Equal(p.Approvers[0].ApproverUserId, a);                  // 现在要审的多的排前面

        var r1 = Assert.Single(p.Items, i => i.RequestNo == "R1");
        Assert.Equal(a, r1.CurrentApproverId);
        Assert.Equal("审批人B", r1.LaterApprovers);
        Assert.True((DateTime.Now - rowA.OldestSubmittedAt!.Value).TotalDays >= 4.9);   // A 手上最久的是 5 天前提交的 R1
        var r2 = Assert.Single(p.Items, i => i.RequestNo == "R2");
        Assert.Equal(b, r2.CurrentApproverId);
        Assert.Equal("", r2.LaterApprovers);
    }

    [Fact]
    public async Task 审批进度_分公司管理员只看到申请人在自己部门范围内的()
    {
        var (a, _, dx, _) = Seed();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var p = await svc.GetApprovalProgressAsync([dx]);

        Assert.Equal(["R1", "R2"], p.Items.Select(i => i.RequestNo).OrderBy(n => n).ToList());   // R4 是 Y 部的，看不到
        Assert.Equal(1, Assert.Single(p.Approvers, r => r.ApproverUserId == a).CurrentCount);
    }

    [Fact]
    public async Task 审批进度_导出两张表_按审批人汇总和未审批明细()
    {
        Seed();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var bytes = AttendanceSystem.Helpers.ExcelExportHelper.ExportApprovalProgress(await svc.GetApprovalProgressAsync(), DateTime.Now);
        Assert.True(bytes.Length > 1000);
        using var wb = new NPOI.XSSF.UserModel.XSSFWorkbook(new MemoryStream(bytes));
        Assert.Equal(2, wb.NumberOfSheets);
        Assert.Equal(2 + 2, wb.GetSheet("按审批人汇总").LastRowNum + 1);      // 标题 + 表头 + 两位审批人
        Assert.Equal(2 + 3, wb.GetSheet("未审批申请明细").LastRowNum + 1);    // 标题 + 表头 + 三张申请
    }
}
