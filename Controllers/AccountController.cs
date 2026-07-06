using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Controllers;

// 「控制器(Controller)」= 接收网页/接口的请求，调用服务去干活，再把结果返回。它本身只做转发，不写业务逻辑。

/// <summary>账号接口：登录、登出、修改密码。</summary>
[Route("api/[controller]")]
[ApiController]
public class AccountController(IUserService userService, IOptions<AppSettingsOptions> appOptions) : ApiControllerBase
{
    /// <summary>登录：工号 + 密码。</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.EmployeeNo) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { Success = false, Message = "工号和密码不能为空" });

        // 交给用户服务校验工号+密码。
        // 注意：账号如果已被停用，ValidateLoginAsync 会直接抛异常而不是返回 null——
        // 这里没有专门 catch 它，是因为 Program.cs 里配置了全局的异常兜底，
        // 会自动把这类异常转成合适的错误提示返回给调用方，不需要每个接口都重复写一遍。
        var user = await userService.ValidateLoginAsync(req.EmployeeNo, req.Password);
        if (user is null)
            return Ok(new { Success = false, Message = "工号或密码错误，请重新输入" });

        // 登录成功：把用户身份写进 Cookie（之后每次访问靠它认人）
        var claims    = AuthClaimsFactory.BuildUserClaims(user);
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties
            {
                IsPersistent = req.RememberMe,   // 勾了“记住我”就长期保存
                ExpiresUtc   = DateTimeOffset.UtcNow.AddHours(appOptions.Value.TokenExpireHours)  // 登录有效期
            });

        return Ok(new
        {
            Success = true,
            Message = "登录成功",
            Data    = new { user.Id, user.EmployeeNo, user.RealName, RoleText = user.Role.ToDisplayName() }
        });
    }

    /// <summary>登出：清掉登录 Cookie。</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { Success = true, RedirectUrl = "/Login" });
    }

    /// <summary>修改密码（需登录）。</summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        if (CurrentUserId == 0) return Unauthorized();   // 没登录直接拒绝
        var ok = await userService.ChangePasswordAsync(CurrentUserId, req.OldPassword, req.NewPassword);
        return Ok(new { Success = ok, Message = ok ? "密码修改成功" : "原密码错误" });
    }
}

// record = 一种简洁的“只读数据载体”，这里用来装请求传来的字段
public record LoginRequest(string EmployeeNo, string Password, bool RememberMe = false);
public record ChangePasswordRequest(string OldPassword, string NewPassword);
