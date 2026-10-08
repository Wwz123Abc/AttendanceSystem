using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Middlewares;

/// <summary>给 HttpContext 增加的便捷方法：一行就能取到当前登录用户。</summary>
public static class CurrentUserExtensions
{
    public static CurrentUser? GetCurrentUser(this HttpContext ctx)
        => ctx.Items["CurrentUser"] as CurrentUser;

    /// <summary>取当前登录用户；调用方都在要求登录的页面/接口里，取不到就是中间件没跑通，直接抛异常，而不是把空引用传下去。</summary>
    public static CurrentUser GetRequiredUser(this HttpContext ctx)
        => ctx.GetCurrentUser() ?? throw new InvalidOperationException("当前请求没有登录用户信息");
}
