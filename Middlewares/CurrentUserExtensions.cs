using Microsoft.AspNetCore.Authentication;
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
}
