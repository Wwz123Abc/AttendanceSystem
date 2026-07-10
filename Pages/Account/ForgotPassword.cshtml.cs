using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Account;

/// <summary>
/// 忘记密码找回页（未登录可访问）：输入工号+手机号 → 钉钉工作通知收验证码 → 输验证码+新密码完成重置。
/// </summary>
[AllowAnonymous]
public class ForgotPasswordModel(IPasswordResetService resetService) : PageModel
{
    [BindProperty] public string EmployeeNo      { get; set; } = string.Empty;
    [BindProperty] public string Phone           { get; set; } = string.Empty;
    [BindProperty] public string Code            { get; set; } = string.Empty;
    [BindProperty] public string NewPassword     { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>是否已经进入"输入验证码+新密码"这一步（验证码已发出）。</summary>
    public bool CodeSent { get; set; }
    /// <summary>密码是否已重置成功。</summary>
    public bool Done { get; set; }

    public string? ErrorMessage   { get; set; }
    public string? SuccessMessage { get; set; }

    public void OnGet() { }

    /// <summary>点"获取验证码"/"重新发送"时执行。</summary>
    public async Task<IActionResult> OnPostSendCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(EmployeeNo) || string.IsNullOrWhiteSpace(Phone))
        {
            ErrorMessage = "请输入工号和手机号";
            return Page();
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(Phone.Trim(), @"^1[3-9]\d{9}$"))
        {
            ErrorMessage = "请输入正确格式的手机号（11 位中国大陆手机号）";
            return Page();
        }

        var (ok, msg) = await resetService.RequestCodeAsync(EmployeeNo.Trim(), Phone.Trim());
        CodeSent = ok;   // 发送成功才进入第二步；失败则停留在第一步让用户核对信息重试
        if (ok) SuccessMessage = msg; else ErrorMessage = msg;
        return Page();
    }

    /// <summary>点"重置密码"时执行。</summary>
    public async Task<IActionResult> OnPostResetAsync()
    {
        CodeSent = true;   // 无论结果如何都停留在第二步表单，不要退回第一步

        // 工号/手机号本应由第一步表单的隐藏字段带过来；这页不需要登录就能访问，
        // 防一手直接绕过第一步发来的畸形请求（缺这两项），避免下面 Trim() 时空引用异常
        if (string.IsNullOrWhiteSpace(EmployeeNo) || string.IsNullOrWhiteSpace(Phone))
        {
            ErrorMessage = "请先完成第一步获取验证码";
            CodeSent     = false;
            return Page();
        }
        if (string.IsNullOrWhiteSpace(Code) || string.IsNullOrWhiteSpace(NewPassword))
        {
            ErrorMessage = "请输入验证码和新密码";
            return Page();
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(Code.Trim(), @"^\d{6}$"))
        {
            ErrorMessage = "验证码应为 6 位数字";
            return Page();
        }
        if (NewPassword.Length < 6)
        {
            ErrorMessage = "新密码不能少于 6 位";
            return Page();
        }
        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "两次输入的密码不一致";
            return Page();
        }

        var (ok, msg) = await resetService.ResetPasswordAsync(
            EmployeeNo.Trim(), Phone.Trim(), Code.Trim(), NewPassword);

        if (ok) { Done = true; SuccessMessage = msg; }
        else    { ErrorMessage = msg; }
        return Page();
    }
}
