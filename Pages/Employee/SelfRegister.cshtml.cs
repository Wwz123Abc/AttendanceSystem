using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Employee;

/// <summary>
/// 新员工扫码登记页（未登录可访问）：填姓名/手机号/身份证号/岗位/劳务公司/住址/紧急联系人，
/// 并可上传身份证照片，提交后进"待确认"，需要等管理员在"员工管理"页审核补全信息才会正式建号。
/// </summary>
[AllowAnonymous]
public class SelfRegisterModel(
    IEmployeeRegistrationService registrationService,
    IWebHostEnvironment env,                    // 用来定位 wwwroot 目录存身份证照片
    IOptions<AppSettingsOptions> appOptions) : PageModel
{
    [BindProperty] public string  RealName              { get; set; } = string.Empty;
    [BindProperty] public string  Phone                 { get; set; } = string.Empty;
    [BindProperty] public string  IdNumber               { get; set; } = string.Empty;
    [BindProperty] public string? Position               { get; set; }
    [BindProperty] public string? ContractCompany        { get; set; }
    [BindProperty] public string? HomeAddress             { get; set; }
    [BindProperty] public string? EmergencyContactName    { get; set; }
    [BindProperty] public string? EmergencyContactPhone   { get; set; }
    [BindProperty] public IFormFile? IdCardPhoto          { get; set; }

    /// <summary>岗位下拉框的固定选项，和服务端校验共用同一份，页面和后台不会对不上。</summary>
    public string[] PositionOptions => IEmployeeRegistrationService.AllowedPositions;

    /// <summary>是否提交成功（成功后页面切换成"提交成功"提示，不再显示表单）。</summary>
    public bool Done { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    /// <summary>点"提交"时执行。</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            if (IdCardPhoto is null || IdCardPhoto.Length == 0)
                throw new InvalidOperationException("请上传身份证照片");
            var photoUrl = await SaveIdCardPhotoAsync();
            await registrationService.SubmitAsync(new SubmitRegistrationDto
            {
                RealName              = RealName,
                Phone                 = Phone,
                IdNumber              = IdNumber,
                Position              = Position,
                ContractCompany       = ContractCompany,
                HomeAddress           = HomeAddress,
                EmergencyContactName  = EmergencyContactName,
                EmergencyContactPhone = EmergencyContactPhone,
                IdCardPhotoUrl        = photoUrl
            });
            Done = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        return Page();
    }

    /// <summary>
    /// 保存上传的身份证照片（这一步在员工还没有工号之前发生，所以按手机号建目录）。
    /// 身份证照片是必填项，调用前 OnPostAsync 已经检查过一定选了文件，这里的空值判断只是兜底。
    /// </summary>
    private async Task<string?> SaveIdCardPhotoAsync()
    {
        if (IdCardPhoto is null || IdCardPhoto.Length == 0) return null;

        if (IdCardPhoto.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("身份证照片不能超过 10MB");
        var ext = Path.GetExtension(IdCardPhoto.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new InvalidOperationException("身份证照片只支持 jpg / png / webp 格式");

        var uploadPath = appOptions.Value.UploadPath.Trim('/', '\\');
        var webRoot    = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir        = Path.Combine(webRoot, uploadPath, "idcards", "registrations", Phone.Trim());
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{ext}";   // 用随机名，避免重名覆盖
        var path     = Path.Combine(dir, fileName);
        await using var fs = System.IO.File.Create(path);
        await IdCardPhoto.CopyToAsync(fs);

        return $"/{uploadPath}/idcards/registrations/{Phone.Trim()}/{fileName}";
    }
}
