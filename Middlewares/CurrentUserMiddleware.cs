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

    // 下面 3 个是便捷判断（页面里直接用，不用每次写一长串条件）
    public bool IsAdmin    => Role == UserRole.Admin;   // 是不是管理员
    public bool IsClerk    => Role == UserRole.Clerk;   // 是不是文员
    public bool CanApprove => Role is UserRole.Admin or UserRole.Clerk  // 有没有审批权限
                                   or UserRole.Supervisor or UserRole.TeamLeader;
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

            // 去数据库查这个账号是否还“在职/有效”
            var isActive = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => (bool?)u.IsActive)
                .FirstOrDefaultAsync();

            // 账号被停用或已删除 → 即使 Cookie 还没过期，也立刻登出并跳回登录页
            if (isActive != true)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                context.Response.Redirect("/Login?disabled=1");
                return;   // 直接结束，不再往后走
            }

            // 把当前用户信息打包存进本次请求的“临时口袋”(Items)，页面可随时取用
            context.Items["CurrentUser"] = new CurrentUser
            {
                UserId            = userId,
                EmployeeNo        = context.User.FindFirstValue(ClaimTypes.Name)     ?? "",
                RealName          = context.User.FindFirstValue("RealName")           ?? "",
                Role              = Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? "Employee"),
                AttendanceGroupId = context.User.FindFirstValue("AttendanceGroupId") is { } g ? int.Parse(g) : null,
                DepartmentId      = context.User.FindFirstValue("DepartmentId")      is { } d ? int.Parse(d) : null
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
