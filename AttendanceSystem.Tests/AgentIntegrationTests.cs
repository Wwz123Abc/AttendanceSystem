using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
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

    private sealed record World(int DeptA, int DeptB, int HqAdmin, int BranchAdmin, int UserA, int UserB, int ConvHq, int ConvBranch, int HqClerk, int ConvClerk);

    /// <summary>总部（不受限）管理员 + 分公司 A 的管理员（范围=部门 A）+ 总部（不受限）文员；员工 A 在 A 部门、员工 B 在 B 部门。</summary>
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
        // DepartmentId 故意挂在分公司 A 下（总部管理员的组织归属常常这样），只靠部门范围挡不住——
        // 必须额外查角色层级（ScopedDepartmentId 才是"能不能被别人管"的口径，见 CanManageAccountCore）
        var hq = new User { EmployeeNo = "HQ1", RealName = "总部管理员", PasswordHash = "x", IsActive = true, Role = UserRole.Admin, DepartmentId = deptA.Id };
        var branch = new User { EmployeeNo = "BR1", RealName = "分公司A管理员", PasswordHash = "x", IsActive = true, Role = UserRole.Admin, DepartmentId = deptA.Id, ScopedDepartmentId = deptA.Id };
        var clerk = new User { EmployeeNo = "CL1", RealName = "总部文员", PasswordHash = "x", IsActive = true, Role = UserRole.Clerk, DepartmentId = deptA.Id };
        var ua = new User { EmployeeNo = "A1", RealName = "员工A", PasswordHash = "x", IsActive = true, DepartmentId = deptA.Id, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        var ub = new User { EmployeeNo = "B1", RealName = "员工B", PasswordHash = "x", IsActive = true, DepartmentId = deptB.Id, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.AddRange(hq, branch, clerk, ua, ub);
        db.SaveChanges();
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = ua.Id, WorkDate = Wed, ShiftScheduleId = shift.Id });
        var c1 = new AgentConversation { UserId = hq.Id, Title = "hq" };
        var c2 = new AgentConversation { UserId = branch.Id, Title = "branch" };
        var c3 = new AgentConversation { UserId = clerk.Id, Title = "clerk" };
        db.AgentConversations.AddRange(c1, c2, c3);
        db.SaveChanges();
        return new World(deptA.Id, deptB.Id, hq.Id, branch.Id, ua.Id, ub.Id, c1.Id, c2.Id, clerk.Id, c3.Id);
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

    // 2026-09-29 第 12 轮审查发现（严重）：撤回（RestoreUserRowAsync）会把快照整行（含角色、管理范围）
    // 直接写回数据库，完全不查角色层级，是唯一能绕过"角色层级检查下沉到 UserService"这道防线的入口。
    // 复现：文员改了普通员工的资料，之后此人被总部提拔成管理员，文员再撤回那次动作，
    // 会把管理员账号原样降级、管理范围被清空。
    [Fact]
    public async Task 撤回不查角色层级_文员改过的员工后来被提拔成管理员_撤回被拦下_角色和范围不变()
    {
        var w = SeedWorld();
        int actionId;
        using (var db = CreateContext())
        {
            var msg = await Tools(db).ExecuteAsync(w.HqClerk, w.ConvClerk, "employee_update_propose",
                Args(new { userId = w.UserA, phone = "13900000000" }), default);
            Assert.Contains("不会自动执行", msg);
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
        }
        using (var db = CreateContext())
            Assert.True((await Actions(db).ReviewAsync(w.HqClerk, actionId, approve: true)).ok);

        // 模拟"总部把员工A提拔成分公司管理员"（跳过应用层直接改库，只是为了复现场景，不是这条测试要验证的点）
        using (var db = CreateContext())
        {
            var ua = await db.Users.FindAsync(w.UserA);
            ua!.Role = UserRole.Admin;
            ua.ScopedDepartmentId = w.DeptA;
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext())
        {
            var (ok, m) = await Actions(db).UndoAsync(w.HqClerk, actionId);
            Assert.False(ok);
            Assert.Contains("角色层级", m);
        }

        using var check = CreateContext();
        var final = await check.Users.AsNoTracking().SingleAsync(u => u.Id == w.UserA);
        Assert.Equal(UserRole.Admin, final.Role);          // 没有被撤回悄悄降级
        Assert.Equal(w.DeptA, final.ScopedDepartmentId);   // 管理范围也没被清空
    }

    [Fact]
    public async Task 撤回_角色全程没变_照常成功()
    {
        var w = SeedWorld();
        int actionId;
        using (var db = CreateContext())
        {
            await Tools(db).ExecuteAsync(w.HqClerk, w.ConvClerk, "employee_update_propose", Args(new { userId = w.UserA, phone = "13900000000" }), default);
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
        }
        using (var db = CreateContext())
            Assert.True((await Actions(db).ReviewAsync(w.HqClerk, actionId, approve: true)).ok);
        using (var db = CreateContext())
        {
            var (ok, m) = await Actions(db).UndoAsync(w.HqClerk, actionId);
            Assert.True(ok, m);
        }
        using var check = CreateContext();
        Assert.Null((await check.Users.AsNoTracking().SingleAsync(u => u.Id == w.UserA)).Phone);   // 恢复成改之前的样子
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

    // ── S1（2026-09-29 全项目审查·严重）：文员/受限管理员不能借助手动总部超管或别人管不到的账号 ──────

    private async Task<int> ProposeAsync(int operatorUserId, int conv, string tool, object args)
    {
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(operatorUserId, conv, tool, Args(args), default);
        Assert.Contains("不会自动执行", msg);
        return (await db.AgentPendingActions.OrderByDescending(a => a.Id).FirstAsync()).Id;
    }

    // 2026-09-29 第 12 轮审查发现：以前"生成待确认动作"这一步完全不查角色层级，文员对总部超管发起
    // 重置密码/删除/拉黑/停用，助手照样会生成一张卡片，只有确认执行那一步才报错——容易误导管理员以为
    // 这个操作是被允许的。现在挡在生成阶段，下面三条改成断言"生成阶段就报错、不生成任何待确认动作"
    // （跟"分公司管理员_对别的分公司的员工提停用_直接报错_不生成动作"是同一种断言方式）。
    // 执行阶段（UserService.EnsureCanManageAsync）那道检查仍然保留、仍然生效，见下面"确认执行这一层"的测试。
    [Fact]
    public async Task 总部文员_不能通过助手重置总部超管的密码()
    {
        var w = SeedWorld();
        string hashBefore;
        using (var db = CreateContext()) hashBefore = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin)).PasswordHash;

        using var db2 = CreateContext();
        var msg = await Tools(db2).ExecuteAsync(w.HqClerk, w.ConvClerk, "password_reset_propose", Args(new { userId = w.HqAdmin }), default);
        Assert.StartsWith("错误", msg);
        Assert.Contains("角色层级", msg);
        Assert.Equal(0, await db2.AgentPendingActions.CountAsync());

        using var check = CreateContext();
        Assert.Equal(hashBefore, (await check.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin)).PasswordHash);
    }

    [Fact]
    public async Task 总部文员_不能通过助手删除总部超管()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(w.HqClerk, w.ConvClerk, "user_delete_propose", Args(new { userId = w.HqAdmin }), default);
        Assert.StartsWith("错误", msg);
        Assert.Contains("角色层级", msg);
        Assert.Equal(0, await db.AgentPendingActions.CountAsync());
        Assert.True(await db.Users.AnyAsync(u => u.Id == w.HqAdmin));   // 人还在
    }

    [Fact]
    public async Task 总部文员_不能通过助手拉黑或停用总部超管()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var blacklistMsg = await Tools(db).ExecuteAsync(w.HqClerk, w.ConvClerk, "user_blacklist_propose", Args(new { userId = w.HqAdmin, action = "blacklist" }), default);
        var toggleMsg = await Tools(db).ExecuteAsync(w.HqClerk, w.ConvClerk, "user_toggle_propose", Args(new { userId = w.HqAdmin, action = "deactivate" }), default);
        Assert.Contains("角色层级", blacklistMsg);
        Assert.Contains("角色层级", toggleMsg);
        Assert.Equal(0, await db.AgentPendingActions.CountAsync());

        var hq = await db.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin);
        Assert.True(hq.IsActive);
        Assert.False(hq.IsBlacklisted);
    }

    /// <summary>确认执行阶段（UserService.EnsureCanManageAsync）那道检查独立覆盖：直接往
    /// AgentPendingActions 表插一条动作（绕开刚加的生成阶段检查），确认执行时仍然要被拦下。</summary>
    [Fact]
    public async Task 总部文员_不能通过助手重置总部超管的密码_确认执行这一层也单独挡住()
    {
        var w = SeedWorld();
        string hashBefore;
        int actionId;
        using (var db = CreateContext())
        {
            hashBefore = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin)).PasswordHash;
            var action = new AgentPendingAction
            {
                ConversationId = w.ConvClerk, ToolName = "password_reset_propose",
                ParamJson = Args(new { userId = w.HqAdmin }),
                SummaryText = "t", Status = AgentActionStatus.Pending, CreatedBy = w.HqClerk,
                CreatedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(15)
            };
            db.AgentPendingActions.Add(action);
            await db.SaveChangesAsync();
            actionId = action.Id;
        }

        using var db2 = CreateContext();
        var (ok, message) = await Actions(db2).ReviewAsync(w.HqClerk, actionId, approve: true);
        Assert.False(ok);
        Assert.Contains("角色层级", message);

        using var check = CreateContext();
        Assert.Equal(hashBefore, (await check.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin)).PasswordHash);
    }

    [Fact]
    public async Task 分公司管理员_同样管不到总部超管_批量启停也会静默跳过()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var users = new UserService(db, new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions,
            new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance)), AppOptions, new DeptScopeService(db), NullLogger<UserService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.DeactivateUserAsync(w.HqAdmin, w.BranchAdmin));
        var n = await users.SetActiveBatchAsync([w.HqAdmin, w.UserA], false, w.BranchAdmin);
        Assert.Equal(1, n);   // 只处理了管得到的 UserA，总部超管被静默跳过、不报错打断整批
        var hq = await db.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin);
        Assert.True(hq.IsActive);
    }

    // 2026-09-29 第 12 轮审查发现：EmployeeUpdateProposeAsync/EmployeeBatchToggleProposeAsync 这两处
    // 生成待确认动作时也只查了部门范围、没查角色层级，同样属于"生成阶段没挡、确认执行才报错"的问题。
    [Fact]
    public async Task 总部文员_不能通过助手修改总部超管的资料()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(w.HqClerk, w.ConvClerk, "employee_update_propose",
            Args(new { userId = w.HqAdmin, phone = "13800000000" }), default);
        Assert.StartsWith("错误", msg);
        Assert.Contains("角色层级", msg);
        Assert.Equal(0, await db.AgentPendingActions.CountAsync());
    }

    [Fact]
    public async Task 总部文员_批量停用混了总部超管_会静默跳过只处理管得到的()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(w.HqClerk, w.ConvClerk, "employee_batch_toggle_propose",
            Args(new { userIds = new[] { w.HqAdmin, w.UserA }, action = "deactivate" }), default);
        Assert.Contains("已生成待确认动作", msg);
        Assert.Contains("已跳过", msg);
        var action = await db.AgentPendingActions.OrderByDescending(a => a.Id).FirstAsync();
        var pIds = System.Text.Json.JsonDocument.Parse(action.ParamJson).RootElement
            .GetProperty("userIds").EnumerateArray().Select(x => x.GetInt32()).ToList();
        Assert.Equal([w.UserA], pIds);   // 总部超管被剔除，只剩员工A
    }

    [Fact]
    public async Task 总部超管_仍然可以正常重置分公司管理员的密码_修复没有误伤合法操作()
    {
        var w = SeedWorld();
        var actionId = await ProposeAsync(w.HqAdmin, w.ConvHq, "password_reset_propose", new { userId = w.BranchAdmin });
        using var db = CreateContext();
        var (ok, message) = await Actions(db).ReviewAsync(w.HqAdmin, actionId, approve: true);
        Assert.True(ok, message);
    }

    // ── M2（角色调整大小写 bug + 角色层级校验一起修）─────────────────────────────

    [Fact]
    public async Task 调整角色_确认执行不再因为大小写报错_总部超管把普通员工调成主管()
    {
        var w = SeedWorld();
        var actionId = await ProposeAsync(w.HqAdmin, w.ConvHq, "employee_role_propose", new { userId = w.UserA, role = "supervisor" });
        using var db = CreateContext();
        var (ok, message) = await Actions(db).ReviewAsync(w.HqAdmin, actionId, approve: true);
        Assert.True(ok, message);
        using var check = CreateContext();
        Assert.Equal(UserRole.Supervisor, (await check.Users.AsNoTracking().SingleAsync(u => u.Id == w.UserA)).Role);
    }

    [Fact]
    public async Task 总部文员_不能通过调整角色把总部超管降级()
    {
        var w = SeedWorld();
        // 这条在"生成待确认动作"这一步就会被拦下（比等确认执行才失败更早、提示更直接），
        // 不会像别的高风险操作那样先落一条提案
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(w.HqClerk, w.ConvClerk, "employee_role_propose", Args(new { userId = w.HqAdmin, role = "employee" }), default);
        Assert.Contains("角色层级", msg);
        Assert.Equal(0, await db.AgentPendingActions.CountAsync());
        using var check = CreateContext();
        Assert.Equal(UserRole.Admin, (await check.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin)).Role);
    }

    [Fact]
    public async Task 总部文员_不能通过调整角色把总部超管降级_确认执行这一层也单独挡住()
    {
        // 防御性测试：即使有一条绕过了提案阶段检查而落库的动作（比如修复上线前生成的旧提案），
        // 确认执行这一步（ExecuteChangeRoleAsync）也要独立拦下来，不能只靠提案阶段那一道检查
        var w = SeedWorld();
        int actionId;
        using (var db = CreateContext())
        {
            var action = new AgentPendingAction
            {
                ConversationId = w.ConvClerk, ToolName = "employee_role_propose",
                ParamJson = Args(new { userId = w.HqAdmin, role = "employee" }),
                SummaryText = "t", Status = AgentActionStatus.Pending, CreatedBy = w.HqClerk,
                CreatedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(15)
            };
            db.AgentPendingActions.Add(action);
            await db.SaveChangesAsync();
            actionId = action.Id;
        }
        using var db2 = CreateContext();
        var (ok, message) = await Actions(db2).ReviewAsync(w.HqClerk, actionId, approve: true);
        Assert.False(ok);
        Assert.Contains("角色层级", message);
        using var check = CreateContext();
        Assert.Equal(UserRole.Admin, (await check.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin)).Role);
    }

    // ── M1：代提交申请遇到配置了审批人名单的考勤组 ──────────────────────────────

    [Fact]
    public async Task 代提交申请_考勤组配了审批人名单_不指定审批人时报错并给出名单_指定后能成功()
    {
        var w = SeedWorld();
        int approverId;
        using (var db = CreateContext())
        {
            var groupId = (await db.Users.Where(u => u.Id == w.UserA).Select(u => u.AttendanceGroupId).SingleAsync())!.Value;
            var approver = new User { EmployeeNo = "AP1", RealName = "审批人甲", PasswordHash = "x", IsActive = true, Role = UserRole.Supervisor, DepartmentId = w.DeptA };
            db.Users.Add(approver);
            db.SaveChanges();
            db.AttendanceGroupApprovers.Add(new AttendanceGroupApprover { AttendanceGroupId = groupId, UserId = approver.Id });
            db.SaveChanges();
            approverId = approver.Id;
        }
        // 提交申请（跟审批一样）用的是真实的 DateTime.Now 做"开始时间不能太久以前"的校验，
        // 不能像别的用例那样用固定的历史日期，改用"现在"往后一点的时间段
        var start = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm");
        var end   = DateTime.Now.AddHours(4).ToString("yyyy-MM-dd HH:mm");
        using (var db = CreateContext())
        {
            var msg = await Tools(db).ExecuteAsync(w.HqAdmin, w.ConvHq, "approval_submit_on_behalf_propose", Args(new
            {
                userId = w.UserA, type = "leave", leaveType = "personal",
                startTime = start, endTime = end, reason = "test"
            }), default);
            Assert.Contains("必须指定其中一位", msg);
            Assert.Contains("审批人甲", msg);
            Assert.Equal(0, await db.AgentPendingActions.CountAsync());   // 没有生成半成品的待确认动作
        }
        int actionId;
        using (var db = CreateContext())
        {
            var msg = await Tools(db).ExecuteAsync(w.HqAdmin, w.ConvHq, "approval_submit_on_behalf_propose", Args(new
            {
                userId = w.UserA, type = "leave", leaveType = "personal",
                startTime = start, endTime = end, reason = "test", approverUserId = approverId
            }), default);
            Assert.Contains("不会自动执行", msg);
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
        }
        using var db2 = CreateContext();
        var (ok, message) = await Actions(db2).ReviewAsync(w.HqAdmin, actionId, approve: true);
        Assert.True(ok, message);
        Assert.True(await db2.ApprovalRequests.AnyAsync(a => a.ApplicantUserId == w.UserA));
    }

    // ── M5：补卡不能补"还没到"的时间点 ───────────────────────────────────────────

    [Fact]
    public async Task 补卡申请_时间点还没到_提交被拒绝()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        // 只加 5 分钟：不会跨到第二天（不然会先撞上"补卡日期不能晚于今天"，跟这条测试想验证的规则是两回事）
        var future = DateTime.Now.AddMinutes(5);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitApprovalAsync(w.UserA, new SubmitApprovalDto
        {
            ApprovalType = AttendanceSystem.Models.Enums.ApprovalType.PunchReplenishment,
            PunchDate = DateOnly.FromDateTime(future), PunchType = AttendanceSystem.Models.Enums.PunchType.ClockOut,
            PunchTime = TimeOnly.FromDateTime(future), Reason = "t"
        }));
        Assert.Contains("还没到", ex.Message);
    }
}
