using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages;

/// <summary>登录页：校验工号/密码，成功后写 Cookie 并按角色跳到对应首页。[AllowAnonymous]=不用登录也能访问。
/// 按 IP 限流：防止对大量不同工号做密码喷洒（跟按工号的失败锁定是两道独立防线）。</summary>
[AllowAnonymous]
[EnableRateLimiting("LoginPolicy")]
public class LoginModel(IUserService userService, IOptions<AppSettingsOptions> appOptions) : PageModel
{
    // [BindProperty]=这几个字段会自动接住页面表单提交上来的值
    [BindProperty] public string EmployeeNo { get; set; } = string.Empty;
    [BindProperty] public string Password   { get; set; } = string.Empty;
    [BindProperty] public bool   RememberMe { get; set; }

    public string? ErrorMessage { get; set; }   // 出错时显示的提示
    public bool    Denied       { get; set; }   // 是不是因为“权限不足”被跳来这

    /// <summary>打开登录页时执行。</summary>
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)   // 已登录就直接进首页（按角色跳，跟登录成功后的去向一致）
            return Redirect(HomeUrl(Enum.TryParse<UserRole>(User.FindFirstValue(ClaimTypes.Role), out var role) ? role : null));
        Denied = Request.Query["denied"] == "1";
        if (Request.Query["disabled"] == "1")
            ErrorMessage = "账号已停用，无法登录，请联系管理员";
        return Page();
    }

    /// <summary>点“登录”按钮提交时执行。</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(EmployeeNo) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "工号和密码不能为空";
            return Page();
        }

        // 工号/密码错误、账号已停用，ValidateLoginAsync 统一返回 null，登录页不区分提示（避免账号被枚举）
        var user = await userService.ValidateLoginAsync(EmployeeNo, Password);   // 校验

        if (user is null)
        {
            ErrorMessage = "工号或密码错误，请重新输入";
            return Page();
        }

        // 登录成功：写入身份 Cookie
        var claims    = AuthClaimsFactory.BuildUserClaims(user);
        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties
            {
                IsPersistent = RememberMe,
                ExpiresUtc   = DateTimeOffset.UtcNow.AddHours(appOptions.Value.TokenExpireHours)
            });

        // 2026-09-24 业务决定：首次登录/被重置密码后不再强制改密码，登录成功直接进首页，
        // 想改的话自己在"修改密码"页改。
        return Redirect(HomeUrl(user.Role));   // 按角色跳首页
    }

    /// <summary>按角色决定登录后进哪个首页。Program.cs 的根路径 "/" 处理也复用这个方法，避免重复一份映射逻辑。</summary>
    internal static string HomeUrl(UserRole? role) => role switch
    {
        UserRole.Admin or UserRole.Clerk                 => "/Admin/Dashboard",            // 管理/文员→看板
        UserRole.Supervisor or UserRole.TeamLeader       => "/Approval/PendingApproval",   // 主管/班组长→待审批
        _                                                => "/Attendance/MyRecord"         // 员工→我的记录（手动打卡已关闭，PunchCard 页面已删除）
    };
}
