using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Middlewares;

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
