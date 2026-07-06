using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Account;

/// <summary>修改密码页：校验输入并提交新密码。</summary>
[Authorize]
public class ChangePasswordModel(IUserService userService) : AppPageModel
{
    [BindProperty] public string OldPassword     { get; set; } = "";   // 原密码
    [BindProperty] public string NewPassword     { get; set; } = "";   // 新密码
    [BindProperty] public string ConfirmPassword { get; set; } = "";   // 再输一遍新密码

    public string? ErrorMessage { get; set; }
    public bool    ShowSuccess  { get; set; }

    public void OnGet() { }   // 打开页面，无需加载数据

    /// <summary>点“提交”时执行：先做几项校验，再改密码。</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(OldPassword) || string.IsNullOrWhiteSpace(NewPassword))
        { ErrorMessage = "密码不能为空"; return Page(); }

        if (NewPassword.Length < 6)
        { ErrorMessage = "新密码不能少于 6 位"; return Page(); }

        if (NewPassword != ConfirmPassword)
        { ErrorMessage = "两次输入的密码不一致"; return Page(); }

        var ok = await userService.ChangePasswordAsync(CurrentUserId, OldPassword, NewPassword);

        if (!ok) { ErrorMessage = "当前密码错误"; return Page(); }   // 原密码不对

        ShowSuccess = true;
        return Page();
    }
}
