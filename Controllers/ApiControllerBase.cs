using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace AttendanceSystem.Controllers;

// 「基类」= 多个控制器共同的“父类”，把它们都要用的公共代码写一份放这里，子类直接拿来用。

/// <summary>
/// API 控制器基类：统一提供“当前登录用户的 Id”，
/// 这样每个接口控制器就不用各写一遍解析登录信息的代码。
/// </summary>
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>
    /// 当前登录用户的编号（Id）。
    /// 登录时这个编号被写进了 Cookie，这里把它读出来；没登录/读不到就返回 0。
    /// </summary>
    protected int CurrentUserId
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
}
