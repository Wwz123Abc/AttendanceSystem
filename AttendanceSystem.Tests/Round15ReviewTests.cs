using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-10-06《全项目审查清单·第 15 轮》逐条核实后修复项的回归测试：
/// H1 迁移脚本同步、M1 看板双扣、M9 汇总并发重试、M11 未来补卡、M6 班次接口校验、M8 附件绑定、
/// X1 新增部门提权、X2 路径穿越，以及补上定位精度容错这块零覆盖的测试（§6 第 1 条）。
/// </summary>
public class Round15ReviewTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());

    public Round15ReviewTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    private static User U(string no, string name) => new()
    {
        EmployeeNo = no, RealName = name, PasswordHash = "x", IsActive = true, HireDate = new DateOnly(2026, 1, 1)
    };

    // ── H1：migrate.sql 必须覆盖 Migrations 目录里的全部迁移 ─────────────────────────

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

    [Fact]
    public void 迁移脚本migrate_sql_覆盖Migrations目录里的全部迁移()
    {
        // 生产建库/升级靠手动执行 migrate.sql；以前最后一条迁移（AddAnnouncementScopeRoles）没生成进脚本，
        // 用脚本建/升的库公告相关页面会整片 500（Unknown column 'Announcement.ScopeRoles'）
        var root = RepoRoot();
        var inCode = Directory.GetFiles(Path.Combine(root, "Migrations"), "*.cs")
            .Select(Path.GetFileName).Select(f => f!)
            .Where(f => !f.EndsWith(".Designer.cs") && !f.EndsWith("ModelSnapshot.cs"))
            .Select(f => f[..^3]).ToHashSet();
        var sql = File.ReadAllText(Path.Combine(root, "migrate.sql"));
        var inScript = Regex.Matches(sql, @"`MigrationId` = '(\d{14}_\w+)'").Select(m => m.Groups[1].Value).ToHashSet();

        Assert.NotEmpty(inCode);
        Assert.Empty(inCode.Except(inScript));    // 脚本里缺的迁移
        Assert.Empty(inScript.Except(inCode));    // 脚本里多出来、代码里没有的
    }

    // ── 定位校验：精度容错分支（§6 第 1 条：以前 5 条用例全走三参重载，accuracy 恒为 null）──────────

    private async Task<int> SeedLocationGroupAsync()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "定位组", EnableLocationPunch = true };
        db.AttendanceGroups.Add(group);
        await db.SaveChangesAsync();
        db.AttendanceGroupLocations.Add(new AttendanceGroupLocation
        {
            AttendanceGroupId = group.Id, LocationName = "总部", Latitude = 30.0, Longitude = 120.0, RadiusMeters = 200
        });
        await db.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task 定位校验_圈外233米_精度50米_把误差算进去后放行_精度20米仍拒绝()
    {
        var gid = await SeedLocationGroupAsync();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var lat = 30.0021;   // 约 233 米，圈半径 200 米

        Assert.True((await svc.ValidateLocationAsync(gid, lat, 120.0, 50)).Valid);      // 233 − 50 = 183 ≤ 200
        var tight = await svc.ValidateLocationAsync(gid, lat, 120.0, 20);               // 233 − 20 = 213 > 200
        Assert.False(tight.Valid);
        Assert.Contains("超出有效范围", tight.Message);
        Assert.False((await svc.ValidateLocationAsync(gid, lat, 120.0, null)).Valid);   // 没传精度：不给容错
    }

    [Fact]
    public async Task 定位校验_浏览器报的精度再大也最多只给100米容错_不能把半径放大到想多远就多远()
    {
        var gid = await SeedLocationGroupAsync();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var lat = 30.0045;   // 约 500 米

        Assert.False((await svc.ValidateLocationAsync(gid, lat, 120.0, 1000)).Valid);   // 封顶 100：500 − 100 = 400 > 200
        Assert.False((await svc.ValidateLocationAsync(gid, lat, 120.0, 100000)).Valid);
    }

    [Fact]
    public void HaversineMeters_纬度差1度约111公里_同一点为0()
    {
        Assert.Equal(111195, AttendanceService.HaversineMeters(30, 120, 31, 120), 0.01 * 111195);
        Assert.Equal(0, AttendanceService.HaversineMeters(30, 120, 30, 120), 1e-6);
    }

    // ── M1：看板"未打卡"人数不能把"半天假且打了上班卡"的人扣两次 ─────────────────────

    [Fact]
    public async Task 看板_半天假打了上班卡的人_未打卡人数仍等于下钻名单条数_不会被扣两次()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        using (var db = CreateContext())
        {
            var halfDay = U("H1", "半天假员工");   // 上午上班、下午请假：既算"出勤"又算"请假"
            var absent  = U("H2", "旷工员工");
            var none    = U("H3", "还没来的员工");
            db.Users.AddRange(halfDay, absent, none);
            await db.SaveChangesAsync();
            db.AttendanceRecords.AddRange(
                new AttendanceRecord { UserId = halfDay.Id, WorkDate = today, ClockInTime = today.ToDateTime(new TimeOnly(8, 30)), AttendanceStatus = AttendanceStatus.OnLeave, LeaveHours = 4 },
                new AttendanceRecord { UserId = absent.Id, WorkDate = today, AttendanceStatus = AttendanceStatus.Absent });
            await db.SaveChangesAsync();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var stats = await svc.GetTodayStatsAsync();
        var list = await svc.GetTodayStatsDetailAsync("notpunched");

        Assert.Equal(3, stats.TotalEmployees);
        Assert.Equal(1, stats.PresentCount);
        Assert.Equal(1, stats.NotPunchedCount);                 // 只有"还没来的员工"；以前被少算成 0
        Assert.Equal(stats.NotPunchedCount, list.Count);        // 卡片数字 = 下钻名单人数
    }

    // ── M11：管理员手动补卡不能补未来的时间点 ───────────────────────────────────────

    [Fact]
    public async Task 管理员手动补卡_未来的时间点被拒绝_过去的正常补()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("P1", "补卡员工");
            db.Users.Add(u);
            await db.SaveChangesAsync();
            uid = u.Id;
        }
        var today = DateOnly.FromDateTime(DateTime.Now);
        var yesterday = today.AddDays(-1);

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AdminAdjustPunchAsync(uid, today, null, DateTime.Now.AddHours(3), null, "管理员"));
        Assert.Contains("还没到", ex.Message);

        await svc.AdminAdjustPunchAsync(uid, yesterday, yesterday.ToDateTime(new TimeOnly(9, 0)), yesterday.ToDateTime(new TimeOnly(18, 0)), null, "管理员");
        using var check = CreateContext();
        Assert.True(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == yesterday && r.ClockOutTime != null));
    }

    // ── M9：月度汇总插入撞唯一索引时自动重试一次，不能整批回滚、当月汇总静默缺失 ──────────────

    private sealed class ConflictOnFirstSave(SqliteConnection conn, int userId, int year, int month) : SaveChangesInterceptor
    {
        private bool _done;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!_done)
            {
                _done = true;   // 模拟"另一个进程在我查完、还没保存之前，抢先把这个人这个月的汇总插进去了"
                using var other = new AttendanceDbContext(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(conn).Options);
                other.MonthlyAttendanceSummaries.Add(new MonthlyAttendanceSummary { UserId = userId, Year = year, Month = month });
                await other.SaveChangesAsync(ct);
            }
            return result;
        }
    }

    [Fact]
    public async Task 月度汇总_保存时撞上别人刚插入的同一条_自动重试_不抛异常_只有一条汇总()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("S1", "汇总员工");
            db.Users.Add(u);
            await db.SaveChangesAsync();
            uid = u.Id;
        }

        var options = new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection)
            .AddInterceptors(new ConflictOnFirstSave(_connection, uid, 2026, 9)).Options;
        using (var db2 = new AttendanceDbContext(options))
        {
            var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
            await svc.GenerateMonthlySummaryAsync(2026, 9, [uid]);   // 以前这里会抛 DbUpdateException
        }

        using var check = CreateContext();
        Assert.Equal(1, await check.MonthlyAttendanceSummaries.CountAsync(s => s.UserId == uid && s.Year == 2026 && s.Month == 9));
    }

    // ── M6：班次 API 的字段范围校验 ───────────────────────────────────────────────

    private static string? ValidateShift(string? name = "白班", int late = 5, int early = 5, int ot = 30, int earliest = 60,
        decimal std = 8, string? color = "#1890ff", string? rest = "0,6", string? mid = null)
        => (string?)typeof(AdminController).GetMethod("ValidateShiftFields", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [name, late, early, ot, earliest, std, color, rest, mid]);

    [Fact]
    public void 班次接口校验_合法配置通过_越界的各字段都被拒绝()
    {
        Assert.Null(ValidateShift());
        Assert.Null(ValidateShift(rest: ""));                       // 一个休息日都不配是允许的
        Assert.Contains("名称", ValidateShift(name: " "));
        Assert.Contains("迟到容忍", ValidateShift(late: 999));
        Assert.Contains("早退容忍", ValidateShift(early: -1));
        Assert.Contains("加班判定", ValidateShift(ot: 500));
        Assert.Contains("标准工时", ValidateShift(std: 0));
        Assert.Contains("标准工时", ValidateShift(std: 100));
        Assert.Contains("颜色", ValidateShift(color: "red"));
        Assert.Contains("休息日", ValidateShift(rest: "0,9"));
        Assert.Contains("过长", ValidateShift(mid: new string('x', 501)));
    }

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

    // ── M14：有考勤/打卡/申请历史的员工只能停用，不能物理删除 ────────────────────────────

    private (UserService Svc, int AdminId) NewUserService(AttendanceDbContext db)
    {
        var admin = U("ADM1", "操作管理员");
        admin.Role = UserRole.Admin;
        db.Users.Add(admin);
        db.SaveChanges();
        var scope = new DeptScopeService(db);
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var zk = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        return (new UserService(db, zk, AppOptions, scope, NullLogger<UserService>.Instance), admin.Id);
    }

    [Fact]
    public async Task 删除员工_没有任何历史数据的空账号_可以删()
    {
        using var db = CreateContext();
        var (svc, adminId) = NewUserService(db);
        var empty = U("EMP0", "误建的空账号");
        db.Users.Add(empty);
        await db.SaveChangesAsync();

        Assert.True(await svc.DeleteUserAsync(empty.Id, adminId));

        using var check = CreateContext();
        Assert.False(await check.Users.AnyAsync(u => u.Id == empty.Id));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("punch")]
    [InlineData("request")]
    public async Task 删除员工_有考勤记录或打卡流水或申请单_只能停用_账号和历史数据都保留(string kind)
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("EMP-" + kind, "有历史的员工");
            db.Users.Add(u);
            await db.SaveChangesAsync();
            uid = u.Id;
            if (kind == "record")
                db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = new DateOnly(2026, 9, 10) });
            else if (kind == "punch")
                db.AttendancePunches.Add(new AttendancePunch { UserId = uid, PunchTime = new DateTime(2026, 9, 10, 8, 30, 0), PunchType = PunchType.ClockIn, IsValid = true });
            else
                db.ApprovalRequests.Add(new ApprovalRequest { RequestNo = "QJ-DEL-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Leave });
            await db.SaveChangesAsync();
        }

        using var db2 = CreateContext();
        var (svc, adminId) = NewUserService(db2);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteUserAsync(uid, adminId));
        Assert.Contains("停用", ex.Message);

        using var check = CreateContext();
        Assert.True(await check.Users.AnyAsync(u => u.Id == uid));                       // 人还在
        Assert.Equal(kind == "record" ? 1 : 0, await check.AttendanceRecords.CountAsync(r => r.UserId == uid));   // 历史没被级联清掉
        Assert.Equal(kind == "punch" ? 1 : 0, await check.AttendancePunches.CountAsync(p => p.UserId == uid));
        Assert.Equal(kind == "request" ? 1 : 0, await check.ApprovalRequests.CountAsync(a => a.ApplicantUserId == uid));
    }

    // ── 阿里云人脸接口：超时重试不能突破总预算（2026-10-06 复核：故障时每人白等约 16.5 秒）──────────

    private static async Task<(int Result, int Calls, long ElapsedMs, Exception? Error)> RunRetry(Func<int, Task<int>> attemptBody)
    {
        // 生产参数按 1/10 缩小：连接 3000 + 读取 5000 = 单次 8 秒 → 100 + 700 = 0.8 秒；总预算 12000 → 1200；退避 500 → 50
        var opt = new AliyunFaceOptions { ConnectTimeoutMs = 100, ReadTimeoutMs = 700, OverallBudgetMs = 1200, RetryBackoffMs = 50, MaxRetryAttempts = 2 };
        var client = new AliyunFaceClient(Options.Create(opt), NullLogger<AliyunFaceClient>.Instance);
        using var budget = new CancellationTokenSource(opt.OverallBudgetMs);
        var calls = 0;
        var method = typeof(AliyunFaceClient).GetMethod("ExecuteWithRetryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(typeof(int));
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await (Task<int>)method.Invoke(client, [new Func<Task<int>>(() => attemptBody(++calls)), Array.Empty<MemoryStream>(), CancellationToken.None, budget.Token])!;
            return (result, calls, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) { return (0, calls, sw.ElapsedMilliseconds, ex); }
    }

    [Fact]
    public async Task 人脸接口_单次尝试就卡满超时_剩余预算不够再来一次_不再重试_按一次的时长失败()
    {
        var r = await RunRetry(async _ => { await Task.Delay(800); throw new System.Net.WebException("operation is timeout"); });

        Assert.IsType<System.Net.WebException>(r.Error);   // 原样抛出（上层会计入熔断），不是被总预算取消
        Assert.Equal(1, r.Calls);                           // 修复前是 2 次（换算到生产就是 16.5 秒）
        Assert.InRange(r.ElapsedMs, 700, 1100);             // 约 0.8 秒；修复前约 1.7 秒
    }

    [Fact]
    public async Task 人脸接口_几乎立刻返回的瞬时错误_剩余预算够_照常重试并成功()
    {
        var r = await RunRetry(n => n == 1 ? throw new System.Net.WebException("operation is timeout") : Task.FromResult(7));

        Assert.Null(r.Error);
        Assert.Equal(7, r.Result);
        Assert.Equal(2, r.Calls);
    }

    // ── 报错带编号：员工把页面上的整段文字发给开发人员，就能按编号在日志里查到原因 ─────────────

    [Fact]
    public void 错误提示_带错误编号时间和异常类型_不展示异常原文_反馈指引在里面()
    {
        var ex = new InvalidOperationException("Duplicate entry 'x' for key 'IX_User_EmployeeNo'");
        var text = AttendanceSystem.Helpers.ErrorReport.Describe(ex, "保存失败，请稍后重试");

        Assert.StartsWith("保存失败，请稍后重试", text);
        Assert.Matches(@"错误编号 E\d{4}-\d{6}-[0-9A-F]{4}", text);          // 形如 E1006-153012-7F3A
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", text);       // 带时间
        Assert.Contains("InvalidOperationException", text);                       // 带类型名
        Assert.Contains("发给开发人员", text);
        Assert.DoesNotContain("IX_User_EmployeeNo", text);                        // 数据库细节不展示给员工（在日志里按编号查）
    }

    [Fact]
    public void 错误提示_每次生成的编号不一样()
    {
        var ex = new Exception("x");
        var ids = Enumerable.Range(0, 50).Select(_ => AttendanceSystem.Helpers.ErrorReport.NewId()).ToHashSet();
        Assert.True(ids.Count >= 45);   // 同一秒内 4 位随机数偶有重复，但绝大多数不同
        Assert.NotEqual(AttendanceSystem.Helpers.ErrorReport.Describe(ex, "a"), AttendanceSystem.Helpers.ErrorReport.Describe(ex, "a"));
    }

    // ── 远程打卡：空白/非 JPEG 的画面不能传给阿里云（2026-10-04 阿里云确认收到的图片是空白的）──────

    [Fact]
    public void 远程打卡_空数据和非JPEG画面被判为不可用_正常JPEG通过()
    {
        // 摄像头没出画面时，前端 toDataURL 返回 "data:,"，服务端解码出来是 0 字节
        Assert.False(AttendanceSystem.Pages.Attendance.RemotePunchModel.IsUsableJpeg(Convert.FromBase64String("")));
        Assert.False(AttendanceSystem.Pages.Attendance.RemotePunchModel.IsUsableJpeg(new byte[] { 0xFF, 0xD8, 0xFF }));   // 太小
        Assert.False(AttendanceSystem.Pages.Attendance.RemotePunchModel.IsUsableJpeg(new byte[4096]));                    // 全 0，不是 JPEG
        var ok = new byte[4096]; ok[0] = 0xFF; ok[1] = 0xD8;
        Assert.True(AttendanceSystem.Pages.Attendance.RemotePunchModel.IsUsableJpeg(ok));
    }

    [Fact]
    public void 人脸接口_失败阶段分类_授权_OSS上传_识别接口三步分得清()
    {
        var typ = typeof(AliyunFaceClient);
        string Classify(string? stack) => (string)typ.GetMethod("ClassifyStageFromStack",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object?[] { stack })!;

        // 2026-10-04 日志里三种真实堆栈
        var oss  = "at Client._postOSSObjectAsync(...) at Client.DetectLivingFaceAdvanceAsync(...)";
        var auth = "at Client.DoRequestAsync(...) at Client.CallApiAsync(...) at Client.DetectLivingFaceAdvanceAsync(...)";
        var api  = "at Client.DoRequestAsync(...) at Client.CallApiAsync(...) at Client.DetectLivingFaceWithOptionsAsync(...) at Client.DetectLivingFaceAdvanceAsync(...)";

        Assert.Contains("OSS", Classify(oss));
        Assert.Contains("授权", Classify(auth));
        Assert.Contains("识别接口", Classify(api));
        Assert.Equal("未知", Classify(null));
    }

    // ── 没排班的人在导出表里加"排班说明"标注（正班工时没封顶、已含工作日加班）─────────────────

    [Fact]
    public void 月度汇总导出_没排班的人最后一列有标注_有排班的人是空()
    {
        var list = new List<AttendanceSystem.Models.DTOs.MonthlySummaryDto>
        {
            new() { EmployeeNo = "002582", RealName = "没排班的人", Year = 2026, Month = 9, NoShiftDays = 22, TotalWorkHours = 245.5m, TotalOvertimeHours = 170.5m },
            new() { EmployeeNo = "002874", RealName = "有排班的人", Year = 2026, Month = 9, NoShiftDays = 0,  TotalWorkHours = 160m,   TotalOvertimeHours = 177m },
        };
        var bytes = AttendanceSystem.Helpers.ExcelExportHelper.ExportMonthlySummary(list, 2026, 9);
        using var wb = new NPOI.XSSF.UserModel.XSSFWorkbook(new MemoryStream(bytes));
        var sheet = wb.GetSheetAt(0);
        Assert.Equal("排班说明", sheet.GetRow(1).GetCell(15).StringCellValue);
        Assert.Contains("没排班22天", sheet.GetRow(2).GetCell(15).StringCellValue);
        Assert.Contains("勿与加班时长直接相加", sheet.GetRow(2).GetCell(15).StringCellValue);
        Assert.Equal("", sheet.GetRow(3).GetCell(15).StringCellValue);
        Assert.Equal("实际工时(h)", sheet.GetRow(1).GetCell(13).StringCellValue);   // 原有列的位置没变
    }

    [Fact]
    public void 模板汇总导出_没排班的人最后一列有标注_原有列位置不变()
    {
        var day = new DateOnly(2026, 9, 28);
        TemplateReportRowDtoBuilder Row(string name, int noShift) => new(name, noShift);
        var rows = new List<AttendanceSystem.Models.DTOs.TemplateReportRowDto>
        {
            Row("没排班的人", 5).Build(day), Row("有排班的人", 0).Build(day),
        };
        var result = new AttendanceSystem.Models.DTOs.TemplateReportResultDto { StartDate = day, EndDate = day, Dates = [day], Rows = rows };
        var bytes = AttendanceSystem.Helpers.ExcelExportHelper.ExportTemplateReport(result);
        using var wb = new NPOI.XSSF.UserModel.XSSFWorkbook(new MemoryStream(bytes));
        var sheet = wb.GetSheetAt(0);
        var nameRow = sheet.GetRow(3);
        var last = nameRow.LastCellNum - 1;
        Assert.Equal("排班说明", nameRow.GetCell(last).StringCellValue);
        Assert.Equal("应出勤天数", nameRow.GetCell(last - 1).StringCellValue.Replace(Environment.NewLine, "").Replace("\n", ""));   // 原来的最后一列"应出勤天数"位置不变
        Assert.Contains("没排班5天", sheet.GetRow(4).GetCell(last).StringCellValue);
        Assert.Equal("", sheet.GetRow(5).GetCell(last).StringCellValue);
    }

    private sealed record TemplateReportRowDtoBuilder(string Name, int NoShift)
    {
        public AttendanceSystem.Models.DTOs.TemplateReportRowDto Build(DateOnly day) => new()
        {
            RealName = Name, NoShiftDays = NoShift,
            DailyHours = [8m], DailyIsNightShift = [false], DailyIsRest = [false],
        };
    }
}
