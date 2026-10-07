using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Pages.Admin;
using AttendanceSystem.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-09-30 第 14 轮审查发现：删除部门时只拦了"下面还有下级部门"（第 11 轮 A4），没拦"下面还有员工"。
/// 部门删除后，数据库外键把员工的 DepartmentId 自动置空成"未分配"，受限管理员看不到"未分配"的人，
/// 这些员工就从他的管理范围里彻底消失了，只能找总部恢复。这里验证受限管理员删除还有员工的部门会被拒。
/// </summary>
public class DepartmentManageTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DepartmentManageTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    private static DepartmentManageModel PageFor(AttendanceDbContext db, CurrentUser cu)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["CurrentUser"] = cu;
        var model = new DepartmentManageModel(db, new DeptScopeService(db), NullLogger<DepartmentManageModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = httpContext }
        };
        return model;
    }

    [Fact]
    public async Task 分公司管理员_删除还有员工的部门_被拒_部门和员工都不变()
    {
        using var db = CreateContext();
        var deptRoot = new Department { DeptName = "分公司A", IsActive = true };
        var deptChild = new Department { DeptName = "分公司A-一部", IsActive = true };
        db.Departments.AddRange(deptRoot, deptChild);
        await db.SaveChangesAsync();
        deptChild.ParentId = deptRoot.Id;
        var branchAdmin = new User { EmployeeNo = "BR1", RealName = "分公司A管理员", PasswordHash = "x", IsActive = true, Role = UserRole.Admin, ScopedDepartmentId = deptRoot.Id };
        var employee = new User { EmployeeNo = "E1", RealName = "员工甲", PasswordHash = "x", IsActive = true, DepartmentId = deptChild.Id };
        db.Users.AddRange(branchAdmin, employee);
        await db.SaveChangesAsync();

        var cu = new CurrentUser { UserId = branchAdmin.Id, Role = UserRole.Admin, ScopedDepartmentId = deptRoot.Id };
        var page = PageFor(db, cu);
        page.DeleteIds = deptChild.Id.ToString();

        await page.OnPostDeleteAsync();

        Assert.Contains("还有员工", page.ErrorMessage);
        using var check = CreateContext();
        Assert.True(await check.Departments.AnyAsync(d => d.Id == deptChild.Id));           // 部门没被删
        Assert.Equal(deptChild.Id, (await check.Users.AsNoTracking().SingleAsync(u => u.Id == employee.Id)).DepartmentId);   // 员工的部门没被清空
    }

    // 自我复核发现（不在第三方审查清单里）：ZKDeviceManage.cshtml.cs 里"受限管理员只能看自己范围内的设备"
    // 也是用 DepartmentId != null 过滤的，删除部门后设备被外键自动置空成"未归类"，跟员工是同一类问题。
    [Fact]
    public async Task 分公司管理员_删除还有考勤机的部门_被拒_部门和设备都不变()
    {
        using var db = CreateContext();
        var deptRoot = new Department { DeptName = "分公司A", IsActive = true };
        var deptChild = new Department { DeptName = "分公司A-一部", IsActive = true };
        db.Departments.AddRange(deptRoot, deptChild);
        await db.SaveChangesAsync();
        deptChild.ParentId = deptRoot.Id;
        var branchAdmin = new User { EmployeeNo = "BR1", RealName = "分公司A管理员", PasswordHash = "x", IsActive = true, Role = UserRole.Admin, ScopedDepartmentId = deptRoot.Id };
        db.Users.Add(branchAdmin);
        var device = new ZKDevice { SN = "DEV001", DepartmentId = deptChild.Id };
        db.ZKDevices.Add(device);
        await db.SaveChangesAsync();

        var cu = new CurrentUser { UserId = branchAdmin.Id, Role = UserRole.Admin, ScopedDepartmentId = deptRoot.Id };
        var page = PageFor(db, cu);
        page.DeleteIds = deptChild.Id.ToString();

        await page.OnPostDeleteAsync();

        Assert.Contains("还有考勤机", page.ErrorMessage);
        using var check = CreateContext();
        Assert.True(await check.Departments.AnyAsync(d => d.Id == deptChild.Id));            // 部门没被删
        Assert.Equal(deptChild.Id, (await check.ZKDevices.AsNoTracking().SingleAsync(d => d.Id == device.Id)).DepartmentId);   // 设备的归属部门没被清空
    }

    [Fact]
    public async Task 分公司管理员_删除没有员工的部门_正常成功()
    {
        using var db = CreateContext();
        var deptRoot = new Department { DeptName = "分公司A", IsActive = true };
        var deptChild = new Department { DeptName = "分公司A-一部", IsActive = true };
        db.Departments.AddRange(deptRoot, deptChild);
        await db.SaveChangesAsync();
        deptChild.ParentId = deptRoot.Id;
        var branchAdmin = new User { EmployeeNo = "BR1", RealName = "分公司A管理员", PasswordHash = "x", IsActive = true, Role = UserRole.Admin, ScopedDepartmentId = deptRoot.Id };
        db.Users.Add(branchAdmin);
        await db.SaveChangesAsync();

        var cu = new CurrentUser { UserId = branchAdmin.Id, Role = UserRole.Admin, ScopedDepartmentId = deptRoot.Id };
        var page = PageFor(db, cu);
        page.DeleteIds = deptChild.Id.ToString();

        await page.OnPostDeleteAsync();

        Assert.NotNull(page.SuccessMessage);
        using var check = CreateContext();
        Assert.False(await check.Departments.AnyAsync(d => d.Id == deptChild.Id));
    }
}
