using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Middlewares;

/// <summary>当前登录用户的信息（从 Cookie 里的身份标签解析出来，供页面使用）。</summary>
public sealed class CurrentUser
{
    public int      UserId            { get; init; }                    // 用户编号
    public string   EmployeeNo        { get; init; } = string.Empty;    // 工号
    public string   RealName          { get; init; } = string.Empty;    // 姓名
    public UserRole Role              { get; init; }                    // 角色
    public int?     AttendanceGroupId { get; init; }                    // 所属考勤组
    public int?     DepartmentId      { get; init; }                    // 所属部门
    public string?  ContractCompany   { get; init; }                    // 劳务公司（合同公司，用于页面水印）

    /// <summary>范围限定部门（仅管理员/文员有意义）：为空 = 不受限（总部超级管理员）；有值 = 只能看/管
    /// 这个部门及其下级范围内的数据（"分公司管理员"）。故意每次请求都从数据库读（不放进登录 Cookie 的
    /// claim），这样总部管理员改了某人的范围/把某人重新设成不受限，对方下一个请求就立刻生效，
    /// 不用等到重新登录——跟 IsActive 停用踢线是同一个"每请求校验一次"的道理，这里本来就已经查库了，
    /// 顺手带上这个字段幂等零成本。</summary>
    public int?     ScopedDepartmentId { get; init; }

    // 下面几个是便捷判断（页面里直接用，不用每次写一长串条件）
    public bool IsAdmin    => Role == UserRole.Admin;   // 是不是管理员
    public bool IsClerk    => Role == UserRole.Clerk;   // 是不是文员
    public bool CanApprove => Role is UserRole.Admin or UserRole.Clerk  // 有没有审批权限
                                   or UserRole.Supervisor or UserRole.TeamLeader;
    public bool IsScoped   => ScopedDepartmentId.HasValue;   // 是不是"分公司管理员"（受部门范围限制）

    /// <summary>是不是"总部超级管理员"：角色为 Admin，且自己没有被设置管理范围。</summary>
    public bool IsHqSuperAdmin => Role == UserRole.Admin && !IsScoped;

    /// <summary>
    /// 角色层级：能不能操作（编辑/停用/拉黑/删除/重置密码）这个目标账号。前提是目标已经在自己的部门范围内——
    /// 光看部门范围不够：总部管理员的 DepartmentId 常常挂在某个分公司下，这个分公司的文员/分公司管理员
    /// "管得到"他，重置密码就能接管总部账号。规则：总部超级管理员可以操作所有人；其他人不能动
    /// "总部管理员"（Admin 且没设范围），文员也不能动任何管理员；分公司管理员之间维持可操作。
    /// </summary>
    public bool CanManageAccount(UserRole targetRole, int? targetScopedDepartmentId) =>
        IsHqSuperAdmin
        || targetRole != UserRole.Admin
        || (targetScopedDepartmentId.HasValue && Role == UserRole.Admin);
}

// 「中间件」= 每个网络请求都会先经过的一道“关卡”。
// 这道关卡做两件事：① 把已停用的账号立刻踢下线；② 把当前用户信息存起来供页面使用。
public class CurrentUserMiddleware(RequestDelegate next)
{
    // 每来一个请求就会执行这个方法
    public async Task InvokeAsync(HttpContext context, AttendanceDbContext db)
    {
        // 只有“已登录”的请求才需要处理
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // 从 Cookie 的身份标签里取出用户编号
            var userId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            // 去数据库查这个账号是否还”在职/有效”，顺便把劳务公司（水印要用）、范围限定部门一起查出来；
            // Role/DepartmentId/AttendanceGroupId 这三个以前是直接读 Cookie 里登录时签发的 claim，
            // 总部把某人降权/调部门/调考勤组后，旧会话在 Cookie 有效期内会一直按旧值走，特别是”降权+
            // 清空范围”这个组合：范围字段下一请求就生效了，但 Role 声明还是 Admin，会被 IsHqSuperAdmin
            // （Role==Admin && !IsScoped）误判成不受限的总部超级管理员，反而是提权方向的窗口。现在这三个
            // 也跟 ScopedDepartmentId 一样每请求查库，改了立刻生效，不用等重新登录。
            var info = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.IsActive, u.ContractCompany, u.ScopedDepartmentId, u.Role, u.DepartmentId, u.AttendanceGroupId })
                .FirstOrDefaultAsync();

            // 账号被停用或已删除 → 即使 Cookie 还没过期，也立刻登出
            if (info?.IsActive != true)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                // 网页请求跳回登录页；/api 接口请求不能跳转，否则调用方拿到的是 302 而不是 401，
                // 判断不出来是"没登录"——跟 Program.cs 里登录 Cookie 的 OnRedirectToLogin 是同一个道理
                if (context.Request.Path.StartsWithSegments("/api"))
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else
                    context.Response.Redirect("/Login?disabled=1");
                return;   // 直接结束，不再往后走
            }

            // 把当前用户信息打包存进本次请求的“临时口袋”(Items)，页面可随时取用
            context.Items["CurrentUser"] = new CurrentUser
            {
                UserId            = userId,
                EmployeeNo        = context.User.FindFirstValue(ClaimTypes.Name)     ?? "",
                RealName          = context.User.FindFirstValue("RealName")           ?? "",
                Role              = info?.Role ?? UserRole.Employee,
                AttendanceGroupId = info?.AttendanceGroupId,
                DepartmentId      = info?.DepartmentId,
                ContractCompany   = info?.ContractCompany,
                ScopedDepartmentId = info?.ScopedDepartmentId
            };
        }

        // 放行，交给下一道处理
        await next(context);
    }
}

/// <summary>给 HttpContext 增加的便捷方法：一行就能取到当前登录用户。</summary>
public static class CurrentUserExtensions
{
    public static CurrentUser? GetCurrentUser(this HttpContext ctx)
        => ctx.Items["CurrentUser"] as CurrentUser;
}
