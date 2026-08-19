using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Options;

namespace AttendanceSystem.Pages.Employee;

/// <summary>
/// 员工自助"人脸采集"页：录一张清晰正脸照，作为以后人脸打卡时的比对参考。
/// 存储/校验逻辑照抄 SelfRegister.cshtml.cs 里 SaveIdCardPhotoAsync 的模式（10MB 上限、jpg/png/webp 白名单）。
/// </summary>
[Authorize]
public class FaceEnrollModel(AttendanceDbContext db, IWebHostEnvironment env, IOptions<AppSettingsOptions> appOptions) : AppPageModel
{
    [BindProperty] public IFormFile? FacePhoto { get; set; }
    [BindProperty] public bool       AgreeConsent { get; set; }

    /// <summary>当前已录入的参考照片地址，没录入过则为空。</summary>
    public string? CurrentPhotoUrl { get; set; }

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    public async Task OnGetAsync()
    {
        CurrentPhotoUrl = await db.Users.Where(u => u.Id == CurrentUserId)
            .Select(u => u.FaceReferencePhotoUrl).FirstOrDefaultAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            if (!AgreeConsent)
                throw new InvalidOperationException("请先勾选同意，再上传人脸照片");
            if (FacePhoto is null || FacePhoto.Length == 0)
                throw new InvalidOperationException("请拍摄或选择一张人脸照片");

            var user = await db.Users.FindAsync(CurrentUserId)
                ?? throw new InvalidOperationException("账号不存在");

            user.FaceReferencePhotoUrl = await SaveFacePhotoAsync(user.FaceReferencePhotoUrl);
            await db.SaveChangesAsync();

            SuccessMessage = "人脸照片已录入，之后打卡时会用这张照片做比对";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        CurrentPhotoUrl = await db.Users.Where(u => u.Id == CurrentUserId)
            .Select(u => u.FaceReferencePhotoUrl).FirstOrDefaultAsync();
        return Page();
    }

    private async Task<string?> SaveFacePhotoAsync(string? oldUrl)
    {
        if (FacePhoto is null || FacePhoto.Length == 0) return oldUrl;

        if (FacePhoto.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("人脸照片不能超过 10MB");
        var ext = Path.GetExtension(FacePhoto.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new InvalidOperationException("人脸照片只支持 jpg / png / webp 格式");

        var uploadPath = appOptions.Value.UploadPath.Trim('/', '\\');
        var webRoot    = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir        = Path.Combine(webRoot, uploadPath, "faces", CurrentUserId.ToString());
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{ext}";
        var path     = Path.Combine(dir, fileName);
        await using (var fs = System.IO.File.Create(path))
            await FacePhoto.CopyToAsync(fs);

        if (!string.IsNullOrEmpty(oldUrl))   // 换了新照片，把旧文件删掉
        {
            var oldPath = Path.Combine(webRoot, oldUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
        }

        return $"/{uploadPath}/faces/{CurrentUserId}/{fileName}";
    }
}
