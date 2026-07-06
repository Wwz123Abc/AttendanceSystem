using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages;

/// <summary>登录页：校验工号/密码，成功后写 Cookie 并按角色跳到对应首页。[AllowAnonymous]=不用登录也能访问。</summary>
[AllowAnonymous]
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
        if (User.Identity?.IsAuthenticated == true)   // 已登录就直接进首页
            return Redirect(HomeUrl(null));
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

        AttendanceSystem.Models.Entities.User? user;
        try
        {
            user = await userService.ValidateLoginAsync(EmployeeNo, Password);   // 校验
        }
        catch (InvalidOperationException ex)   // 账号已停用会抛异常
        {
            ErrorMessage = ex.Message;
            return Page();
        }

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

        return Redirect(HomeUrl(user.Role));   // 按角色跳首页
    }

    /// <summary>按角色决定登录后进哪个首页。</summary>
    private static string HomeUrl(UserRole? role) => role switch
    {
        UserRole.Admin or UserRole.Clerk                 => "/Admin/Dashboard",            // 管理/文员→看板
        UserRole.Supervisor or UserRole.TeamLeader       => "/Approval/PendingApproval",   // 主管/班组长→待审批
        _                                                => "/Attendance/PunchCard"        // 员工→打卡页
    };
}
