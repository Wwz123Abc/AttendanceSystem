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

    /// <summary>2026-09-30 复核发现：punch_adjust_propose 之前只查了部门范围，跟第 12 轮修的 S1 是同一类
    /// 漏洞漏掉的一个工具——总部文员（不受部门范围限制）能借助手给总部超管补卡。生成阶段和确认执行阶段
    /// 都要单独验证（后者是绕开生成阶段检查的防御纵深，模式跟"重置密码"那条 S1 回归测试一致）。</summary>
    [Fact]
    public async Task 总部文员_不能通过助手给总部超管补卡_生成阶段就被拒()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var msg = await Tools(db).ExecuteAsync(w.HqClerk, w.ConvClerk, "punch_adjust_propose",
            Args(new { userId = w.HqAdmin, workDate = "2026-09-16", clockOut = "18:00" }), default);
        Assert.Contains("角色层级", msg);
        Assert.Equal(0, await db.AgentPendingActions.CountAsync());
    }

    [Fact]
    public async Task 总部文员_不能通过助手给总部超管补卡_确认执行这一层也单独挡住()
    {
        var w = SeedWorld();
        int actionId;
        using (var db = CreateContext())
        {
            var action = new AgentPendingAction
            {
                ConversationId = w.ConvClerk, ToolName = "punch_adjust_propose",
                ParamJson = Args(new { userId = w.HqAdmin, workDate = "2026-09-16", clockOut = "18:00" }),
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
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == w.HqAdmin));   // 没有留下补录的记录
    }

    [Fact]
    public async Task 助手给夜班补下班卡_填第二天早上的时间点_落到第二天_不再时间倒挂()
    {
        // 2026-09-30 复核发现：ParseTime 只会把时间拼到 workDate 当天，夜班下班卡填"第二天早上几点"
        // 会被拼在当天，变成下班早于上班的"时间倒挂"，工时算成 0——修复后要跟补卡审批同一套顺延规则。
        var w = SeedWorld();
        using (var db = CreateContext())
        {
            var nightShift = new ShiftSchedule
            {
                ShiftName = "夜班", AttendanceGroupId = (await db.Users.FindAsync(w.UserA))!.AttendanceGroupId!.Value,
                WorkStartTime = new TimeOnly(20, 0), WorkEndTime = new TimeOnly(8, 0), IsCrossDay = true,
                LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 8, RestDaysOfWeek = ""
            };
            db.ShiftSchedules.Add(nightShift);
            await db.SaveChangesAsync();
            var assign = await db.ShiftAssignments.SingleAsync(a => a.UserId == w.UserA && a.WorkDate == Wed);
            assign.ShiftScheduleId = nightShift.Id;
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = w.UserA, WorkDate = Wed, ClockInTime = Wed.ToDateTime(new TimeOnly(20, 0)), AttendanceStatus = AttendanceStatus.Normal });
            await db.SaveChangesAsync();
        }
        int actionId;
        using (var db = CreateContext())
        {
            var msg = await Tools(db).ExecuteAsync(w.HqAdmin, w.ConvHq, "punch_adjust_propose",
                Args(new { userId = w.UserA, workDate = Wed.ToString("yyyy-MM-dd"), clockOut = "08:00" }), default);
            Assert.Contains("不会自动执行", msg);
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
        }
        using (var db = CreateContext())
        {
            var (ok, m) = await Actions(db).ReviewAsync(w.HqAdmin, actionId, approve: true);
            Assert.True(ok, m);
        }
        using var check = CreateContext();
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == w.UserA && r.WorkDate == Wed);
        Assert.Equal(Wed.AddDays(1).ToDateTime(new TimeOnly(8, 0)), rec.ClockOutTime);   // 落到第二天，不是当天08:00
        Assert.True(rec.ActualWorkHours > 0);
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

    // 2026-09-30 第 14 轮审查发现：ReviewAsync 执行失败后没有 db.ChangeTracker.Clear()，AppendLogAsync
    // 里的 SaveChangesAsync() 会把同一个 DbContext 里"执行到一半、已经改在内存里"的实体一起存进去——
    // ExecuteUpdateEmployeeAsync 先把 RealName/Phone 直接改在 FindAsync 查出来的跟踪实体上，
    // 再调 UpdateUserAsync 里的 EnsureCanManageAsync 做角色层级校验，校验失败抛异常时改动已经在内存里了。
    [Fact]
    public async Task 总部文员_不能通过助手修改总部超管的资料_确认执行这一层也单独挡住_数据库里姓名手机号都不变()
    {
        var w = SeedWorld();
        string nameBefore;
        string? phoneBefore;
        int actionId;
        using (var db = CreateContext())
        {
            var hq = await db.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin);
            nameBefore = hq.RealName;
            phoneBefore = hq.Phone;
            var action = new AgentPendingAction
            {
                ConversationId = w.ConvClerk, ToolName = "employee_update_propose",
                ParamJson = Args(new { userId = w.HqAdmin, realName = "改名了", phone = "13800000000" }),
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
        var after = await check.Users.AsNoTracking().SingleAsync(u => u.Id == w.HqAdmin);
        Assert.Equal(nameBefore, after.RealName);    // 执行失败，改到一半的姓名不能落库
        Assert.Equal(phoneBefore, after.Phone);      // 手机号同理
    }

    // 同一个 bug（AppendLogAsync 顺手存下执行到一半的改动）另一条复现路径：ExecuteHandleApprovalAsync
    // 走的是 ApprovalService.HandleApprovalAsync → AttendanceService.UpdateAttendanceAfterApprovalAsync，
    // 补卡单当天没有考勤记录时会先 db.AttendanceRecords.Add(record) 建一条空记录，再检查"补卡时间还没到"，
    // 检查不通过时抛异常——虽然 HandleApprovalAsync 自己的数据库事务会正确回滚，但这条 Add() 是直接加在
    // AgentActionService 和 ApprovalService 共用的同一个 DbContext 的内存 ChangeTracker 里，不属于那个事务，
    // 不清空的话 AppendLogAsync 的 SaveChanges 会把它顺手存进库，变成一条谁都没申请过的空白考勤记录。
    [Fact]
    public async Task 助手审批补卡单_回写时补卡时间还没到而失败_不留下空白考勤记录()
    {
        var w = SeedWorld();
        var tomorrow = DateOnly.FromDateTime(DateTime.Now.AddDays(1));
        int requestId;
        using (var db = CreateContext())
        {
            var req = new ApprovalRequest
            {
                ApplicantUserId = w.UserA, ApprovalType = ApprovalType.PunchReplenishment,
                PunchDate = tomorrow, PunchTime = new TimeOnly(9, 0), PunchType = PunchType.ClockIn,
                ApprovalStatus = ApprovalStatus.Pending, RequestNo = "BK-TEST-001", Reason = "t"
            };
            db.ApprovalRequests.Add(req);
            await db.SaveChangesAsync();
            db.ApprovalSteps.Add(new ApprovalStep
            {
                ApprovalRequestId = req.Id, ApproverUserId = w.HqAdmin, StepOrder = 1, ApprovalStatus = ApprovalStatus.Pending
            });
            await db.SaveChangesAsync();
            requestId = req.Id;
        }

        int actionId;
        using (var db = CreateContext())
        {
            var msg = await Tools(db).ExecuteAsync(w.HqAdmin, w.ConvHq, "approval_handle_propose",
                Args(new { requestId, approve = true }), default);
            Assert.Contains("不会自动执行", msg);
            actionId = (await db.AgentPendingActions.SingleAsync()).Id;
        }
        using (var db = CreateContext())
        {
            var (ok, message) = await Actions(db).ReviewAsync(w.HqAdmin, actionId, approve: true);
            Assert.False(ok);
            Assert.Contains("还没到", message);
        }

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == w.UserA && r.WorkDate == tomorrow));   // 没有多出空白记录
        Assert.Equal(ApprovalStatus.Pending, (await check.ApprovalRequests.AsNoTracking().SingleAsync(r => r.Id == requestId)).ApprovalStatus);   // 单子仍是待审批
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

    // ── 2026-10-06 第三方复核：异常清单口径 / 日期跨度 / 错误文案 / 假别校验 ─────────────────

    [Fact]
    public async Task 异常清单_状态已是旷工的记录_字段里残留的迟到分钟不再报出来_跟报表口径一致()
    {
        var w = SeedWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = w.UserA, WorkDate = Wed, AttendanceStatus = AttendanceStatus.Absent, LateMinutes = 811 });
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = w.UserB, WorkDate = Wed, AttendanceStatus = AttendanceStatus.Late, LateMinutes = 12, ClockInTime = Wed.ToDateTime(new TimeOnly(8, 42)) });
            await db.SaveChangesAsync();
        }
        using var db2 = CreateContext();
        var msg = await Tools(db2).ExecuteAsync(w.HqAdmin, w.ConvHq, "attendance_anomaly_list",
            Args(new { start = "2026-09-16", end = "2026-09-16" }), default);
        Assert.Contains("旷工", msg);
        Assert.DoesNotContain("811", msg);        // 状态是旷工，残留的迟到分钟不算数
        Assert.Contains("迟到12分", msg);          // 状态就是迟到的，照常报
    }

    [Fact]
    public async Task 异常清单_日期跨度超过92天_直接报错让模型分段_92天以内照常查()
    {
        var w = SeedWorld();
        using var db = CreateContext();
        var tooLong = await Tools(db).ExecuteAsync(w.HqAdmin, w.ConvHq, "attendance_anomaly_list",
            Args(new { start = "2000-01-01", end = "2100-01-01" }), default);
        Assert.StartsWith("错误", tooLong);
        Assert.Contains("92", tooLong);

        var edgeOk = await Tools(db).ExecuteAsync(w.HqAdmin, w.ConvHq, "attendance_anomaly_list",
            Args(new { start = "2026-07-01", end = "2026-09-30" }), default);   // 正好 92 天
        Assert.DoesNotContain("跨度", edgeOk);
        var edgeOver = await Tools(db).ExecuteAsync(w.HqAdmin, w.ConvHq, "attendance_anomaly_list",
            Args(new { start = "2026-06-30", end = "2026-09-30" }), default);   // 93 天
        Assert.Contains("跨度", edgeOver);
    }

    [Fact]
    public async Task AGENT错误文案_本程序抛的业务提示原样给用户_框架或数据库抛的换成通用文案()
    {
        // 本程序自己抛的 InvalidOperationException：服务层专门给用户看的提示，保留
        using var ctx = CreateContext();
        var svc = new AttendanceService(ctx, AppOptions, NullLogger<AttendanceService>.Instance);
        var ours = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AdminAdjustPunchAsync(1, Wed, null, null, new string('x', 101), null));
        Assert.Equal(ours.Message, AgentErrorText.ForUser(ours));

        // 框架自己抛的同一个类型（可能带出内部细节）→ 通用文案
        var framework = Assert.Throws<InvalidOperationException>(() => new List<int>().First());
        Assert.Equal(AgentErrorText.Generic, AgentErrorText.ForUser(framework));

        // 数据库错误（带表名/约束名）→ 通用文案，原文不会出现
        var dbErr = new Exception("Duplicate entry 'x' for key 'IX_User_EmployeeNo' (table `User`)");
        var shown = AgentErrorText.ForUser(dbErr);
        Assert.Equal(AgentErrorText.Generic, shown);
        Assert.DoesNotContain("IX_User_EmployeeNo", shown);
    }

    [Fact]
    public async Task 代提交请假_确认执行时假别数值不合法_直接拒绝_不强转落库()
    {
        var w = SeedWorld();
        int actionId;
        using (var db = CreateContext())
        {
            var action = new AgentPendingAction
            {
                ConversationId = w.ConvHq, ToolName = "approval_submit_on_behalf_propose",
                ParamJson = Args(new
                {
                    userId = w.UserA, type = "leave", leaveType = 99,
                    startTime = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm"),
                    endTime = DateTime.Now.AddHours(4).ToString("yyyy-MM-dd HH:mm"), reason = "t"
                }),
                SummaryText = "t", Status = AgentActionStatus.Pending, CreatedBy = w.HqAdmin,
                CreatedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(15)
            };
            db.AgentPendingActions.Add(action);
            await db.SaveChangesAsync();
            actionId = action.Id;
        }
        using var db2 = CreateContext();
        var (ok, message) = await Actions(db2).ReviewAsync(w.HqAdmin, actionId, approve: true);
        Assert.False(ok);
        Assert.Contains("请假类型", message);
        using var check = CreateContext();
        Assert.False(await check.ApprovalRequests.AnyAsync(r => r.ApplicantUserId == w.UserA));   // 没有落库
    }
}
