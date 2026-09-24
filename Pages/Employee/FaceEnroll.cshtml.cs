using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace AttendanceSystem.Pages.Employee;

/// <summary>
/// 员工自助"人脸采集"页：录一张清晰正脸照，作为以后人脸打卡时的比对参考。
/// 存储/校验逻辑照抄 SelfRegister.cshtml.cs 里 SaveIdCardPhotoAsync 的模式（10MB 上限、jpg/png/webp 白名单）。
/// </summary>
[Authorize]
public class FaceEnrollModel(
    AttendanceDbContext db,
    IWebHostEnvironment env,
    IAliyunFaceClient faceClient,
    IOptions<AppSettingsOptions> appOptions,
    IOptions<AliyunFaceOptions> faceOptions,
    ILogger<FaceEnrollModel> logger) : AppPageModel
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

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            if (!AgreeConsent)
                throw new InvalidOperationException("请先勾选同意，再上传人脸照片");
            if (FacePhoto is null || FacePhoto.Length == 0)
                throw new InvalidOperationException("请拍摄或选择一张人脸照片");

            var user = await db.Users.FindAsync(CurrentUserId)
                ?? throw new InvalidOperationException("账号不存在");

            // 录入过就不能自己再换：参考照是远程打卡防代打的唯一依据，员工要是能随时自己换，
            // 把它换成同事的脸，同事就能用自己的脸替他打卡（1:1 比对只能证明"镜头前的人=参考照上的人"）。
            // 确实要换（换脸、拍得不好）由管理员在「员工管理」里"清除人脸照片"后重新录入（2026-09-24 第 11 轮审查，用户确认）
            if (!string.IsNullOrEmpty(user.FaceReferencePhotoUrl))
                throw new InvalidOperationException("已经录入过人脸照片，如需更换请联系管理员在「员工管理」里清除后重新录入");

            // 顺序：先写新文件（不删旧的）→ 写库 → 成功了才删旧文件；写库失败就把新文件回收掉。
            // 以前是先删旧照片再写库，写库一失败旧照片已经没了、库里却还指向它，这个员工的远程打卡就
            // 彻底不能用了（2026-09-24 审查修复）
            var oldUrl = user.FaceReferencePhotoUrl;
            var newUrl = await SaveFacePhotoAsync(oldUrl, ct);
            var wroteNew = newUrl != oldUrl;
            user.FaceReferencePhotoUrl = newUrl;
            try { await db.SaveChangesAsync(); }
            catch
            {
                if (wroteNew) DeleteFaceFiles(newUrl);
                throw;
            }
            if (wroteNew) DeleteFaceFiles(oldUrl);

            SuccessMessage = "人脸照片已录入，之后打卡时会用这张照片做比对";
        }
        // AliyunFaceApiException 也是给用户看的安全提示（阿里云调用失败/图片不合格等），
        // 跟自己抛的 InvalidOperationException 一样可以直接展示，不属于要隐藏的原始报错
        catch (Exception ex) when (ex is InvalidOperationException or AliyunFaceApiException)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "录入人脸参考照片失败，UserId={UserId}", CurrentUserId);
            ErrorMessage = "操作失败，请稍后重试";
        }
        CurrentPhotoUrl = await db.Users.Where(u => u.Id == CurrentUserId)
            .Select(u => u.FaceReferencePhotoUrl).FirstOrDefaultAsync();
        return Page();
    }

    /// <summary>阿里云人脸识别接口要求图片分辨率不超过 4096x4096，手机拍的原图（尤其近几年的高像素机型）
    /// 很容易超过这个上限，超了会导致这个人以后每次远程打卡都失败。这里存的"留档"版统一缩到长边不超过 1600
    /// （人脸识别够用，也方便管理端查看），顺带把文件体积压下来。</summary>
    private const int MainPhotoMaxDimension = 1600;

    /// <summary>专门给"1:1 人脸比对"用的瘦身版：比对只需要脸部区域清楚，800 像素完全够用，
    /// 体积只有留档版的三四分之一——每次远程打卡都要把这张图传给阿里云，越小传得越快、越省。</summary>
    private const int VerifyPhotoMaxDimension = 800;

    private const string VerifySuffix = "_verify";

    private async Task<string?> SaveFacePhotoAsync(string? oldUrl, CancellationToken ct)
    {
        if (FacePhoto is null || FacePhoto.Length == 0) return oldUrl;

        if (FacePhoto.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("人脸照片不能超过 10MB");
        var ext = Path.GetExtension(FacePhoto.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new InvalidOperationException("人脸照片只支持 jpg / png / webp 格式");

        // 先在内存里把两个尺寸都生成出来，做完人脸质量检测确认合格了，再落盘——
        // 检测不通过时不能已经写了一半文件，导致明明拒绝了却留下垃圾文件/误改了参考照片。
        byte[] mainBytes, verifyBytes;
        await using (var srcStream = FacePhoto.OpenReadStream())
        using (var image = await Image.LoadAsync(srcStream, ct))
        {
            image.Mutate(x => x.AutoOrient());

            using var mainImage = image.CloneAs<SixLabors.ImageSharp.PixelFormats.Rgb24>();
            if (mainImage.Width > MainPhotoMaxDimension || mainImage.Height > MainPhotoMaxDimension)
                mainImage.Mutate(x => x.Resize(new ResizeOptions
                { Mode = ResizeMode.Max, Size = new Size(MainPhotoMaxDimension, MainPhotoMaxDimension) }));
            using (var ms = new MemoryStream())
            {
                await mainImage.SaveAsync(ms, new JpegEncoder { Quality = 85 }, ct);
                mainBytes = ms.ToArray();
            }

            using var verifyImage = image.CloneAs<SixLabors.ImageSharp.PixelFormats.Rgb24>();
            if (verifyImage.Width > VerifyPhotoMaxDimension || verifyImage.Height > VerifyPhotoMaxDimension)
                verifyImage.Mutate(x => x.Resize(new ResizeOptions
                { Mode = ResizeMode.Max, Size = new Size(VerifyPhotoMaxDimension, VerifyPhotoMaxDimension) }));
            using (var ms = new MemoryStream())
            {
                await verifyImage.SaveAsync(ms, new JpegEncoder { Quality = 80 }, ct);
                verifyBytes = ms.ToArray();
            }
        }

        // 拿瘦身版去做质量检测就够了（脸部清不清楚跟这点分辨率差异关系不大），检测本身也更快
        var detect = await faceClient.DetectFaceAsync(verifyBytes, ct);
        if (detect.FaceCount == 0)
            throw new InvalidOperationException("没有检测到人脸，请正对摄像头、保证光线充足后重拍");
        if (detect.FaceCount > 1)
            throw new InvalidOperationException("照片里检测到多张人脸，请单人拍摄后重试");
        if (detect.FaceSizeRatio < faceOptions.Value.EnrollMinFaceSizeRatio)
            throw new InvalidOperationException("人脸在照片里太小，请靠近一些、让脸部占满画面中央后重拍");
        if (detect.QualityScore < faceOptions.Value.EnrollMinQualityScore)
            throw new InvalidOperationException("照片不够清晰（可能模糊、光线太暗或遮挡），请正对摄像头、光线充足处重拍");
        if (detect.MaxPoseAngle > faceOptions.Value.EnrollMaxPoseAngle)
            throw new InvalidOperationException("请正脸拍摄，不要侧脸或歪头");

        var uploadPath  = appOptions.Value.UploadPath.Trim('/', '\\');
        var privateRoot = PrivateFileStorage.GetRoot(env);   // 人脸照片是敏感文件，存在 wwwroot 之外，见 PrivateFilesController
        var dir         = Path.Combine(privateRoot, uploadPath, "faces", CurrentUserId.ToString());
        Directory.CreateDirectory(dir);

        var fileName       = $"{Guid.NewGuid():N}.jpg";   // 统一转成 jpg 存，不用再按原始格式分支
        var verifyFileName = $"{Path.GetFileNameWithoutExtension(fileName)}{VerifySuffix}.jpg";

        await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, fileName), mainBytes, ct);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, verifyFileName), verifyBytes, ct);

        // 不在这里删旧文件：要等调用方把新地址成功写进数据库之后才能删（见 OnPostAsync）
        return $"/{uploadPath}/faces/{CurrentUserId}/{fileName}";
    }

    /// <summary>删掉一张人脸参考照的两个文件（留档版 + 比对版）。只在写库成功换掉旧照片、或写库失败要回收新文件时调用；
    /// 文件不存在/删除失败都只记日志，不影响主流程。</summary>
    private void DeleteFaceFiles(string? url) => PrivateFileStorage.DeleteFaceReferenceFiles(env, url, logger);
}
