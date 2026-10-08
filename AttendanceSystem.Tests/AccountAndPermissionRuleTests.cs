using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AttendanceSystem.Models.Exceptions;

namespace AttendanceSystem.Tests;

/// <summary>
/// 账号与权限：登录锁定到期清零、管理账号角色层级、全局异常放行规则、自助登记姓名校验。
/// </summary>
public class AccountAndPermissionRuleTests : SqliteTestBase
{
    // ── ⑤ 登录锁定到期后失败次数清零 ───────────────────────────────────

    [Fact]
    public async Task 登录锁定到期后_输错一次只算第一次失败_不会立刻再锁()
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = new User { EmployeeNo = "LK1", RealName = "锁定测试", PasswordHash = UserService.HashPassword("Right#123"), IsActive = true,
                FailedLoginCount = 5, LockedUntil = DateTime.Now.AddMinutes(-1) };   // 上一轮锁定刚刚到期
            db.Users.Add(u); db.SaveChanges(); uid = u.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new UserService(db, null!, AppOptions, new DeptScopeService(db), NullLogger<UserService>.Instance);
            Assert.Null(await svc.ValidateLoginAsync("LK1", "wrong"));
        }
        using var check = CreateContext();
        var after = await check.Users.SingleAsync(u => u.Id == uid);
        Assert.Equal(1, after.FailedLoginCount);   // 以前是 6
        Assert.Null(after.LockedUntil);            // 以前立刻又被锁 15 分钟
    }

    // ── ⑥ 管理账号的角色层级 ───────────────────────────────────────────

    [Fact]
    public void 角色层级_分公司账号不能操作范围为空的总部文员_总部账号不受影响()
    {
        var branchAdmin = Cu(UserRole.Admin, 10);
        var branchClerk = Cu(UserRole.Clerk, 10);
        var hqAdmin     = Cu(UserRole.Admin, null);
        var hqClerk     = Cu(UserRole.Clerk, null);

        Assert.False(branchAdmin.CanManageAccount(UserRole.Clerk, null));   // 范围为空的总部文员：分公司管理员不能动
        Assert.False(branchClerk.CanManageAccount(UserRole.Clerk, null));
        Assert.True(branchAdmin.CanManageAccount(UserRole.Clerk, 10));      // 有范围的分公司文员：可以
        Assert.True(branchAdmin.CanManageAccount(UserRole.Employee, null)); // 普通员工/主管/班组长不受影响
        Assert.True(branchAdmin.CanManageAccount(UserRole.Supervisor, null));
        Assert.True(hqAdmin.CanManageAccount(UserRole.Clerk, null));        // 总部管理员可以操作所有人
        Assert.True(hqClerk.CanManageAccount(UserRole.Clerk, null));        // 总部文员之间不受影响
        // 原有规则不变：总部管理员（Admin 且没范围）只有总部超管能动；分公司管理员之间可以互相操作
        Assert.False(branchAdmin.CanManageAccount(UserRole.Admin, null));
        Assert.False(hqClerk.CanManageAccount(UserRole.Admin, 10));
        Assert.True(branchAdmin.CanManageAccount(UserRole.Admin, 20));
    }

    // ── ⑦ 全局异常：只放行本程序自己抛出的业务提示 ─────────────────────

    [Fact]
    public async Task 异常来源判断_本程序抛出的算业务提示_框架抛出的不算()
    {
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var ours = await Assert.ThrowsAsync<BusinessException>(
            () => svc.AdminAdjustPunchAsync(1, Mon, null, null, new string('x', 101), null));
        Assert.Equal(typeof(AttendanceService).Assembly, ours.TargetSite?.DeclaringType?.Assembly);

        var framework = Assert.Throws<InvalidOperationException>(() => new List<int>().First());   // 框架自己抛的同一个类型
        Assert.NotEqual(typeof(AttendanceService).Assembly, framework.TargetSite?.DeclaringType?.Assembly);
    }

    [Fact]
    public void BusinessException_是业务提示_AGENT文案直接放行_原有的InvalidOperationException捕获照常接得住()
    {
        var biz = new BusinessException("工号已存在");
        Assert.True(AgentErrorText.IsBusinessMessage(biz));        // 没抛出过（没有 TargetSite）也认
        Assert.Equal("工号已存在", AgentErrorText.ForUser(biz));
        Assert.IsAssignableFrom<InvalidOperationException>(biz);   // 老的 catch (InvalidOperationException) 不受影响
    }

    // ── ⑧ 自助登记：姓名为空（模型绑定给 null）时给出正确提示 ───────────

    [Fact]
    public async Task 自助登记_姓名为null_提示请填写姓名_而不是空引用()
    {
        using var db = CreateContext();
        var svc = new EmployeeRegistrationService(db, new DeptScopeService(db));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitAsync(new SubmitRegistrationDto
        {
            RealName = null!, Phone = "13800000000", IdNumber = "110101199001011234"
        }));
        Assert.Equal("请填写姓名", ex.Message);
    }

    // ── 岗位选项：扫码登记页、服务端校验、后台新增员工页共用同一份，新增了"其他" ──

    [Fact]
    public void 岗位选项_包含其他_且排在最后()
    {
        var all = AttendanceSystem.Services.Interfaces.IEmployeeRegistrationService.AllowedPositions;
        Assert.Equal("其他", all[^1]);
        Assert.Contains("普工", all);
    }

    [Theory]
    [InlineData("其他", false)]            // 通过岗位校验，后面才因为别的必填项（劳务公司）停下
    [InlineData("随便写的岗位", true)]      // 不在选项里：岗位校验就拦下
    public async Task 扫码登记_岗位必须是选项之一_其他可以(string position, bool rejectedByPosition)
    {
        using var db = CreateContext();
        var svc = new EmployeeRegistrationService(db, new DeptScopeService(db));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.SubmitAsync(new SubmitRegistrationDto
        {
            RealName = "测试", Phone = "13800000000", IdNumber = "110101199001011234", Position = position
        }));
        Assert.Equal(rejectedByPosition, ex.Message == "请选择正确的岗位选项");
    }

    // ── 角色层级：CurrentUser.CanManageAccount ─────────────────────────────

    [Theory]
    // 操作者角色, 操作者范围, 目标角色, 目标范围, 期望
    [InlineData(UserRole.Admin, null, UserRole.Admin,    null, true)]    // 总部超管：谁都能操作
    [InlineData(UserRole.Admin, null, UserRole.Admin,    5,    true)]
    [InlineData(UserRole.Admin, 5,    UserRole.Admin,    null, false)]   // 分公司管理员不能动总部管理员（重置密码接管总部账号）
    [InlineData(UserRole.Admin, 5,    UserRole.Admin,    5,    true)]    // 分公司管理员之间维持可操作
    [InlineData(UserRole.Admin, 5,    UserRole.Clerk,    5,    true)]
    [InlineData(UserRole.Clerk, 5,    UserRole.Admin,    5,    false)]   // 文员不能动任何管理员
    [InlineData(UserRole.Clerk, 5,    UserRole.Admin,    null, false)]
    [InlineData(UserRole.Clerk, null, UserRole.Admin,    null, false)]   // 不带范围的文员同样不行
    [InlineData(UserRole.Clerk, 5,    UserRole.Employee, 5,    true)]    // 文员管普通员工照常
    [InlineData(UserRole.Clerk, 5,    UserRole.Supervisor, null, true)]
    public void 角色层级判断(UserRole opRole, int? opScope, UserRole targetRole, int? targetScope, bool expected)
        => Assert.Equal(expected, Cu(opRole, opScope).CanManageAccount(targetRole, targetScope));

    [Fact]
    public void 总部超管判定()
    {
        Assert.True(Cu(UserRole.Admin, null).IsHqSuperAdmin);
        Assert.False(Cu(UserRole.Admin, 3).IsHqSuperAdmin);
        Assert.False(Cu(UserRole.Clerk, null).IsHqSuperAdmin);   // 不带范围的文员不是超管
    }
}
