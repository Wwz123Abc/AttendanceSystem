using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Employee;

/// <summary>
/// 新员工扫码登记页（未登录可访问）：填姓名/手机号/身份证号/岗位/劳务公司/住址/紧急联系人，
/// 并可上传身份证照片，提交后进"待确认"，需要等管理员在"员工管理"页审核补全信息才会正式建号。
/// 按 IP 限流：匿名接口没有账号锁定这道保护，防止被刷传大量登记照片占满存储。
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("SelfRegisterPolicy")]
public class SelfRegisterModel(
    IEmployeeRegistrationService registrationService,
    IWebHostEnvironment env,                    // 用来定位 wwwroot 目录存身份证照片
    IOptions<AppSettingsOptions> appOptions,
    ILogger<SelfRegisterModel> logger) : PageModel
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

    /// <summary>意向部门 id：每个分公司的二维码链接自带各自的 deptId，随表单一起提交回来，
    /// 确保"扫哪个分公司的码，登记就归到哪个分公司"，不用员工自己填、也填不错。</summary>
    [BindProperty(SupportsGet = true)] public int? DeptId { get; set; }

    /// <summary>岗位下拉框的固定选项，和服务端校验共用同一份，页面和后台不会对不上。</summary>
    public string[] PositionOptions => IEmployeeRegistrationService.AllowedPositions;

    /// <summary>是否提交成功（成功后页面切换成"提交成功"提示，不再显示表单）。</summary>
    public bool Done { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    /// <summary>点"提交"时执行。</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        // 只跟踪"这次请求自己刚保存的照片"是哪个物理路径——如果下面 SubmitAsync 的业务校验
        // （姓名/身份证号格式等）后失败，要把这次刚落盘的照片删掉，不然匿名接口反复提交、
        // 每次都能在 SubmitAsync 失败前留下一张最大 10MB 的孤儿照片，白白占用磁盘
        string? savedPhotoPath = null;
        try
        {
            if (IdCardPhoto is null || IdCardPhoto.Length == 0)
                throw new InvalidOperationException("请上传身份证照片");
            // 手机号会被直接拼进身份证照片的存储目录名（见 SaveIdCardPhotoAsync），这一步在
            // SubmitAsync 做完整格式校验之前就先跑了；这个页面未登录也能提交，如果这里只兜底判个"非空"，
            // 填个 "../../xxx" 之类的值就能越出预期目录建文件夹/写文件（匿名可达的路径穿越写面）。
            // 所以这里要在真正用 Phone 建目录之前，把 SubmitAsync 里那套手机号格式校验提前搬过来一份，
            // 校验通过后 Phone 只可能是纯 11 位数字，天然不含任何路径分隔符/穿越字符。
            if (string.IsNullOrWhiteSpace(Phone) || !System.Text.RegularExpressions.Regex.IsMatch(Phone.Trim(), @"^1[3-9]\d{9}$"))
                throw new InvalidOperationException("请输入正确格式的手机号（11 位中国大陆手机号）");
            var photoUrl = await SaveIdCardPhotoAsync();
            savedPhotoPath = photoUrl is null ? null : Path.Combine(PrivateFileStorage.GetRoot(env), photoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
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
                IdCardPhotoUrl        = photoUrl,
                DepartmentId          = DeptId
            });
            Done = true;
        }
        catch (Exception ex)
        {
            // 照片已经落盘了，但后面的 SubmitAsync 校验没通过——把刚保存的这张删掉，不留孤儿文件；
            // 删除失败也只是记日志，不能让"清理失败"盖过用户本来该看到的原始错误提示
            if (savedPhotoPath is not null)
            {
                try { System.IO.File.Delete(savedPhotoPath); }
                catch (Exception delEx) { logger.LogWarning(delEx, "自助登记提交失败后清理孤儿照片失败：{Path}", savedPhotoPath); }
            }
            if (ex is InvalidOperationException)
            {
                ErrorMessage = ex.Message;
            }
            else
            {
                logger.LogError(ex, "自助登记提交失败");
                ErrorMessage = "提交失败，请稍后重试";
            }
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

        // 只看扩展名不够——匿名提交的这个接口可以随便把任意文件改成 .jpg 后缀上传。这里额外看一眼
        // 文件头（魔数），确认内容真的是对应的图片格式，跟考勤机上传照片（ZKDeviceController）
        // 已经在做的同一道校验保持一致。
        var header = new byte[12];
        await using (var headerStream = IdCardPhoto.OpenReadStream())
            await headerStream.ReadExactlyAsync(header.AsMemory(0, (int)Math.Min(12, IdCardPhoto.Length)));
        if (!ImageValidationHelper.IsValidImageHeader(ext, header))
            throw new InvalidOperationException("身份证照片文件内容与格式不符，请重新选择图片文件");

        var uploadPath = appOptions.Value.UploadPath.Trim('/', '\\');
        var dir        = Path.Combine(PrivateFileStorage.GetRoot(env), uploadPath, "idcards", "registrations", Phone.Trim());
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{ext}";   // 用随机名，避免重名覆盖
        var path     = Path.Combine(dir, fileName);
        await using var fs = System.IO.File.Create(path);
        await IdCardPhoto.CopyToAsync(fs);

        return $"/{uploadPath}/idcards/registrations/{Phone.Trim()}/{fileName}";
    }
}
