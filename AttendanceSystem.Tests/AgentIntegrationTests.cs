using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 管理员智能助手（AGENT）合入主项目后的联调验收（对应 docs/AGENT验收清单.md 第 3、6 节里不依赖大模型的部分）：
/// 分公司越权拦截、"停用→撤回"要清掉停用时间、"补下班卡→撤回"要连工时和状态一起还原。
/// 这里绕过大模型，直接调工具执行器（提案）和动作服务（确认/撤回），验证的是主项目里的真实业务逻辑。
/// </summary>
public class AgentIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());
    private static readonly DateOnly Wed = new(2026, 9, 16);   // 过去的一个周三

    public AgentIntegrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    private sealed record World(int DeptA, int DeptB, int HqAdmin, int BranchAdmin, int UserA, int UserB, int ConvHq, int ConvBranch);

    /// <summary>总部（不受限）管理员 + 分公司 A 的管理员（范围=部门 A）；员工 A 在 A 部门、员工 B 在 B 部门。</summary>
    private World SeedWorld()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "白班组" };
        db.AttendanceGroups.Add(group);
        var deptA = new Department { DeptName = "分公司A", IsActive = true };
        var deptB = new Department { DeptName = "分公司B", IsActive = true };
        db.Departments.AddRange(deptA, deptB);
        db.SaveChanges();
        var shift = new ShiftSchedule
        {
            ShiftName = "白班", AttendanceGroupId = group.Id, WorkStartTime = new TimeOnly(8, 30), WorkEndTime = new TimeOnly(17, 30),
            LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 8, RestDaysOfWeek = "0,6"
        };
        db.ShiftSchedules.Add(shift);
        var hq = new User { EmployeeNo = "HQ1", RealName = "总部管理员", PasswordHash = "x", IsActive = true, Role = UserRole.Admin };
        var branch = new User { EmployeeNo = "BR1", RealName = "分公司A管理员", PasswordHash = "x", IsActive = true, Role = UserRole.Admin, DepartmentId = deptA.Id, ScopedDepartmentId = deptA.Id };
        var ua = new User { EmployeeNo = "A1", RealName = "员工A", PasswordHash = "x", IsActive = true, DepartmentId = deptA.Id, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        var ub = new User { EmployeeNo = "B1", RealName = "员工B", PasswordHash = "x", IsActive = true, DepartmentId = deptB.Id, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.AddRange(hq, branch, ua, ub);
        db.SaveChanges();
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = ua.Id, WorkDate = Wed, ShiftScheduleId = shift.Id });
        var c1 = new AgentConversation { UserId = hq.Id, Title = "hq" };
        var c2 = new AgentConversation { UserId = branch.Id, Title = "branch" };
        db.AgentConversations.AddRange(c1, c2);
        db.SaveChanges();
        return new World(deptA.Id, deptB.Id, hq.Id, branch.Id, ua.Id, ub.Id, c1.Id, c2.Id);
    }

    private AgentToolExecutor Tools(AttendanceDbContext db) =>
        new(db, new DeptScopeService(db), NullLogger<AgentToolExecutor>.Instance);

    private AgentActionService Actions(AttendanceDbContext db)
    {
        var scope = new DeptScopeService(db);
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var zk = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        var users = new UserService(db, zk, AppOptions, scope, NullLogger<UserService>.Instance);
        return new AgentActionService(db, scope, users, att, new EmployeeRegistrationService(db, scope),
            new AttendanceGroupService(db), new ApprovalService(db, att, AppOptions), new AnnouncementService(db),
            NullLogger<AgentActionService>.Instance);
    }

    private static string Args(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    // ── 越权（验收清单 §6）────────────────────────────────────────────────

    [Fact]
    public async Task 分公司管理员_对别的分公司的员工提停用_直接报错_不生成动作()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(w.BranchAdmin, w.ConvBranch, "user_toggle_propose", Args(new { userId = w.UserB, action = "deactivate" }), default);
        Assert.StartsWith("错误", msg);
        Assert.Contains("不在你的管理范围", msg);
        Assert.Equal(0, await db.AgentPendingActions.CountAsync());
    }

    [Fact]
    public async Task 分公司管理员_对别的分公司的员工补卡_直接报错()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(w.BranchAdmin, w.ConvBranch, "punch_adjust_propose",
            Args(new { userId = w.UserB, workDate = "2026-09-16", clockOut = "18:00" }), default);
        Assert.StartsWith("错误", msg);
        Assert.Equal(0, await db.AgentPendingActions.CountAsync());
    }

    [Fact]
    public async Task 分公司管理员_查员工只看到自己范围内的人()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(w.BranchAdmin, w.ConvBranch, "user_search", "{}", default);
        Assert.Contains("员工A", msg);
        Assert.DoesNotContain("员工B", msg);
        var hqMsg = await Tools(db).ExecuteAsync(w.HqAdmin, w.ConvHq, "user_search", "{}", default);
        Assert.Contains("员工B", hqMsg);   // 总部账号不受限
    }

    [Fact]
    public async Task 分公司管理员_不能提改管理范围()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(w.BranchAdmin, w.ConvBranch, "scope_change_propose", Args(new { userId = w.UserA, departmentId = w.DeptB }), default);
        Assert.StartsWith("错误", msg);
        Assert.Equal(0, await db.AgentPendingActions.CountAsync());
    }

    [Fact]
    public async Task 确认执行时会再校验一次范围_员工在提案后被调走则执行失败且数据不变()
    {
        var w = SeedWorld();
        int actionId;
        using (var db = CreateContext())
        {
            await Tools(db).ExecuteAsync(w.BranchAdmin, w.ConvBranch, "user_toggle_propose", Args(new { userId = w.UserA, action = "deactivate" }), default);
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
            (await db.Users.FindAsync(w.UserA))!.DepartmentId = w.DeptB;   // 提案之后员工被调去了别的分公司
            await db.SaveChangesAsync();
        }
        using var db2 = CreateContext();
        var (ok, _) = await Actions(db2).ReviewAsync(w.BranchAdmin, actionId, approve: true);
        Assert.False(ok);
        Assert.True((await db2.Users.AsNoTracking().SingleAsync(u => u.Id == w.UserA)).IsActive);
    }

    // ── 停用 → 撤回：停用时间要清空（验收清单 §3）────────────────────────────

    [Fact]
    public async Task 停用员工_确认后生效_撤回后恢复在职且停用时间被清空()
    {
        var w = SeedWorld();
        int actionId;
        using (var db = CreateContext())
        {
            var msg = await Tools(db).ExecuteAsync(w.BranchAdmin, w.ConvBranch, "user_toggle_propose", Args(new { userId = w.UserA, action = "deactivate" }), default);
            Assert.Contains("不会自动执行", msg);
            Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.Id == w.UserA)).IsActive);   // 只是提案，数据没动
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
        }
        using (var db = CreateContext())
        {
            var (ok, m) = await Actions(db).ReviewAsync(w.BranchAdmin, actionId, approve: true);
            Assert.True(ok, m);
        }
        using (var db = CreateContext())
        {
            var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.UserA);
            Assert.False(u.IsActive);
            Assert.NotNull(u.DeactivatedAt);
            var (ok, m) = await Actions(db).UndoAsync(w.BranchAdmin, actionId);
            Assert.True(ok, m);
        }
        using (var db = CreateContext())
        {
            var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.UserA);
            Assert.True(u.IsActive);
            Assert.Null(u.DeactivatedAt);
        }
    }

    [Fact]
    public async Task 拒绝的动作不改数据()
    {
        var w = SeedWorld();
        int actionId;
        using (var db = CreateContext())
        {
            await Tools(db).ExecuteAsync(w.BranchAdmin, w.ConvBranch, "user_toggle_propose", Args(new { userId = w.UserA, action = "deactivate" }), default);
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
        }
        using var db2 = CreateContext();
        var (ok, _) = await Actions(db2).ReviewAsync(w.BranchAdmin, actionId, approve: false);
        Assert.True(ok);
        Assert.True((await db2.Users.AsNoTracking().SingleAsync(u => u.Id == w.UserA)).IsActive);
        Assert.Equal(AgentActionStatus.Rejected, (await db2.AgentPendingActions.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task 别人的会话里的动作不能确认()
    {
        var w = SeedWorld();
        int actionId;
        using (var db = CreateContext())
        {
            await Tools(db).ExecuteAsync(w.BranchAdmin, w.ConvBranch, "user_toggle_propose", Args(new { userId = w.UserA, action = "deactivate" }), default);
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
        }
        using var db2 = CreateContext();
        var (ok, _) = await Actions(db2).ReviewAsync(w.HqAdmin, actionId, approve: true);   // 总部账号也不能替别人的会话点确认
        Assert.False(ok);
        Assert.True((await db2.Users.AsNoTracking().SingleAsync(u => u.Id == w.UserA)).IsActive);
    }

    // ── 补卡 → 撤回：工时和状态一起还原（验收清单 §3）────────────────────────

    [Fact]
    public async Task 补下班卡_工时出现_撤回后打卡工时状态全部回到补卡前()
    {
        var w = SeedWorld();
        using (var db = CreateContext())
        {
            // 只有上班卡的一天：缺下班卡，工时还没结算
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = w.UserA, WorkDate = Wed, ClockInTime = Wed.ToDateTime(new TimeOnly(8, 25)),
                AttendanceStatus = AttendanceStatus.NotPunched, ActualWorkHours = 0
            });
            db.SaveChanges();
        }
        int actionId;
        using (var db = CreateContext())
        {
            var msg = await Tools(db).ExecuteAsync(w.BranchAdmin, w.ConvBranch, "punch_adjust_propose",
                Args(new { userId = w.UserA, workDate = "2026-09-16", clockOut = "17:40", remark = "验收" }), default);
            Assert.Contains("不会自动执行", msg);
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
        }
        AttendanceStatus statusBefore;
        using (var db = CreateContext())
            statusBefore = (await db.AttendanceRecords.AsNoTracking().SingleAsync()).AttendanceStatus;
        using (var db = CreateContext())
        {
            var (ok, m) = await Actions(db).ReviewAsync(w.BranchAdmin, actionId, approve: true);
            Assert.True(ok, m);
        }
        using (var db = CreateContext())
        {
            var r = await db.AttendanceRecords.AsNoTracking().SingleAsync();
            Assert.NotNull(r.ClockOutTime);
            Assert.True(r.ActualWorkHours > 0);                     // 补卡后工时被重算出来
            var (ok, m) = await Actions(db).UndoAsync(w.BranchAdmin, actionId);
            Assert.True(ok, m);
        }
        using (var db = CreateContext())
        {
            var r = await db.AttendanceRecords.AsNoTracking().SingleAsync();
            Assert.Null(r.ClockOutTime);                            // 下班卡没了
            Assert.Equal(0m, r.ActualWorkHours);                    // 工时回到 0，不残留
            Assert.Equal(statusBefore, r.AttendanceStatus);         // 状态回到补卡前
            Assert.NotNull(r.ClockInTime);                          // 上班卡还在
        }
    }
}
