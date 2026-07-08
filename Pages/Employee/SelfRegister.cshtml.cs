using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Employee;

/// <summary>
/// 新员工扫码登记页（未登录可访问）：填姓名/手机号/身份证号提交，
/// 提交后进"待确认"，需要等管理员在"员工管理"页审核补全信息才会正式建号。
/// </summary>
[AllowAnonymous]
public class SelfRegisterModel(IEmployeeRegistrationService registrationService) : PageModel
{
    [BindProperty] public string RealName { get; set; } = string.Empty;
    [BindProperty] public string Phone    { get; set; } = string.Empty;
    [BindProperty] public string IdNumber { get; set; } = string.Empty;

    /// <summary>是否提交成功（成功后页面切换成"提交成功"提示，不再显示表单）。</summary>
    public bool Done { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    /// <summary>点"提交"时执行。</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await registrationService.SubmitAsync(new SubmitRegistrationDto
            {
                RealName = RealName,
                Phone    = Phone,
                IdNumber = IdNumber
            });
            Done = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        return Page();
    }
}
