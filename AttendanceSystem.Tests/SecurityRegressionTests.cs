using System.Reflection;
using System.Text.Json;
using AttendanceSystem.Controllers;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 安全回归：新增部门不能夹带导航集合提权、私有文件路径穿越与审批人附件绑定。
/// </summary>
public class SecurityRegressionTests : SqliteTestBase
{
    // ── X1：新增部门接口不能夹带 Users 导航集合造出超管账号 ─────────────────────────────

    private AdminController NewAdminController(AttendanceDbContext db)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["CurrentUser"] = new CurrentUser { UserId = 1, Role = UserRole.Clerk };   // 不受限的文员
        return new AdminController(null!, new DeptScopeService(db), db)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx }
        };
    }

    [Fact]
    public async Task 新增部门接口_请求体里夹带的Users被忽略_不会多出账号_部门本身正常创建()
    {
        using var db = CreateContext();
        var ctl = NewAdminController(db);
        var dept = new Department
        {
            DeptName = "新部门", IsActive = true,
            Users = [new User { EmployeeNo = "EVIL1", RealName = "夹带的超管", PasswordHash = "x", Role = UserRole.Admin, IsActive = true }]
        };

        var result = await ctl.CreateDepartment(dept);

        Assert.IsType<OkObjectResult>(result);
        using var check = CreateContext();
        Assert.True(await check.Departments.AnyAsync(d => d.DeptName == "新部门"));
        Assert.False(await check.Users.AnyAsync(u => u.EmployeeNo == "EVIL1"));   // 以前会落库一个 Role=Admin、不受范围限制的账号
    }

    // ── X2 / M8：私有文件读取 ───────────────────────────────────────────────────────

    private sealed class StubEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "t";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "att-test-" + Guid.NewGuid());
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private PrivateFilesController NewFilesController(AttendanceDbContext db, int userId, UserRole role = UserRole.Employee)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["CurrentUser"] = new CurrentUser { UserId = userId, Role = role };
        return new PrivateFilesController(new StubEnv(), db, new DeptScopeService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = ctx }
        };
    }

    [Theory]
    [InlineData("approvals/1/../idcards/E1/a.jpg")]
    [InlineData("approvals/1/./a.jpg")]
    [InlineData("faces/1/../../appsettings.json")]
    public async Task 私有文件_路径里带点或双点段_一律拒绝_不让判权和读文件看到两条不同的路径(string relativePath)
    {
        using var db = CreateContext();
        var result = await NewFilesController(db, 1).Get(relativePath);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task 私有文件_借自己的approvals目录加双点段去读别人的身份证照_真实文件存在时也拿不到()
    {
        // X2 的真实利用链：判权看"approvals/{自己}"，读文件却是归一化后的 idcards/{别人}/xxx。
        // 真的把文件放在磁盘上——文件不存在时前后两种写法都是 404，测不出区别
        var env = new StubEnv();
        var dir = Path.Combine(env.ContentRootPath, "PrivateUploads", "uploads", "idcards", "E1");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "secret.jpg"), [1, 2, 3]);
        try
        {
            using var db = CreateContext();
            var ctx = new DefaultHttpContext();
            ctx.Items["CurrentUser"] = new CurrentUser { UserId = 1, Role = UserRole.Employee };
            var ctl = new PrivateFilesController(env, db, new DeptScopeService(db))
            { ControllerContext = new ControllerContext { HttpContext = ctx } };

            var result = await ctl.Get("approvals/1/../idcards/E1/secret.jpg");

            Assert.IsType<NotFoundResult>(result);   // 以前会返回 PhysicalFileResult，把别人的身份证照发出去
        }
        finally { Directory.Delete(env.ContentRootPath, true); }
    }

    [Fact]
    public async Task 私有文件_审批人只能读自己审批过的那几张单上的附件_不能读该申请人之后的所有附件()
    {
        int applicantId, approverId;
        using (var db = CreateContext())
        {
            var applicant = U("AP1", "申请人");
            var approver  = U("AP2", "审批人");
            var other     = U("AP3", "另一个审批人");
            db.Users.AddRange(applicant, approver, other);
            await db.SaveChangesAsync();
            applicantId = applicant.Id; approverId = approver.Id;

            var mine = new ApprovalRequest { RequestNo = "QJ-1", ApplicantUserId = applicantId, ApprovalType = ApprovalType.Leave,
                AttachmentUrls = JsonSerializer.Serialize(new[] { $"/uploads/approvals/{applicantId}/aaa111.jpg" }) };
            var later = new ApprovalRequest { RequestNo = "QJ-2", ApplicantUserId = applicantId, ApprovalType = ApprovalType.Leave,
                AttachmentUrls = JsonSerializer.Serialize(new[] { $"/uploads/approvals/{applicantId}/bbb222.jpg" }) };
            db.ApprovalRequests.AddRange(mine, later);
            await db.SaveChangesAsync();
            db.ApprovalSteps.AddRange(
                new ApprovalStep { ApprovalRequestId = mine.Id, ApproverUserId = approverId, StepOrder = 1, ApprovalStatus = ApprovalStatus.Approved },
                new ApprovalStep { ApprovalRequestId = later.Id, ApproverUserId = other.Id, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending });
            await db.SaveChangesAsync();
        }

        using var db2 = CreateContext();
        var ctl = NewFilesController(db2, approverId);
        var check = typeof(PrivateFilesController).GetMethod("CanAccessApprovalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task<bool> CanRead(string file) =>
            await (Task<bool>)check.Invoke(ctl, [new[] { "approvals", applicantId.ToString(), file }, false])!;

        Assert.True(await CanRead("aaa111.jpg"));    // 他审批的那张单上的附件
        Assert.False(await CanRead("bbb222.jpg"));   // 同一个申请人、别人审批的那张单上的附件
        Assert.False(await CanRead("unknown.jpg"));  // 根本没记录在任何申请单上的文件
    }

    [Fact]
    public void 上传图片按文件头校验_TIFF等其他格式改成jpg后缀也过不去()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1 };
        var png  = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var tiffLittle = new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0, 0, 0 };   // 小端 TIFF
        var tiffBig    = new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8, 0, 0, 0, 0 };   // 大端 TIFF
        var bigTiff    = new byte[] { 0x49, 0x49, 0x2B, 0x00, 8, 0, 0, 0, 0, 0, 0, 0 };   // BigTIFF（有漏洞的解码路径之一）

        Assert.True(AttendanceSystem.Helpers.ImageValidationHelper.IsValidImageHeader(".jpg", jpeg));
        Assert.True(AttendanceSystem.Helpers.ImageValidationHelper.IsValidImageHeader(".png", png));
        foreach (var fake in new[] { tiffLittle, tiffBig, bigTiff })
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp" })
                Assert.False(AttendanceSystem.Helpers.ImageValidationHelper.IsValidImageHeader(ext, fake));
        Assert.False(AttendanceSystem.Helpers.ImageValidationHelper.IsValidImageHeader(".jpg", png));   // 内容和后缀不一致
    }
}
