using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Services.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 临时测试页：验证阿里云人脸识别（活体检测 + 1:1 比对）接口配置是否正确、调用是否正常。
/// 只是联调用的诊断工具，不是正式功能，等打卡流程正式接入人脸识别后可以删掉。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class FaceTestModel(IAliyunFaceClient faceClient) : PageModel
{
    // 联调诊断页，只挡"连续快速点击"这种最直接的刷调用量方式，不引入新的数据库表/字段——
    // 用静态字典记每个管理员上次调用的时间就够了，进程重启会清零，不需要持久化。
    private static readonly ConcurrentDictionary<int, DateTime> LastCallAtByUser = new();
    private const int MinSecondsBetweenCalls = 10;

    [BindProperty] public IFormFile? ReferencePhoto { get; set; }
    [BindProperty] public IFormFile? LivePhoto       { get; set; }

    public bool?   IsLive     { get; set; }
    public bool?   IsMatch    { get; set; }
    public double? Confidence { get; set; }
    public string? FailReason { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task OnPostAsync()
    {
        try
        {
            var userId = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : 0;
            var now    = DateTime.Now;
            if (LastCallAtByUser.TryGetValue(userId, out var lastAt) && (now - lastAt).TotalSeconds < MinSecondsBetweenCalls)
                throw new InvalidOperationException($"调用太频繁，请等 {MinSecondsBetweenCalls} 秒后再试（这是付费接口，联调时也要控制调用频率）");
            LastCallAtByUser[userId] = now;

            if (ReferencePhoto is null || ReferencePhoto.Length == 0 || LivePhoto is null || LivePhoto.Length == 0)
                throw new InvalidOperationException("请上传两张照片");
            // 跟正式的人脸录入页（FaceEnroll）保持一致的上限，避免手机原图（经常上千万像素）直接超过
            // 阿里云接口 4096×4096 的限制、或者白白拉高这个付费接口的调用体积/延迟
            if (ReferencePhoto.Length > 10 * 1024 * 1024 || LivePhoto.Length > 10 * 1024 * 1024)
                throw new InvalidOperationException("单张照片不能超过 10MB");

            var refBytes  = await ResizeIfNeededAsync(ReferencePhoto);
            var liveBytes = await ResizeIfNeededAsync(LivePhoto);

            var result = await faceClient.VerifyAsync(refBytes, liveBytes);
            IsLive     = result.IsLive;
            IsMatch    = result.IsMatch;
            Confidence = result.Confidence;
            FailReason = result.FailReason;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>把上传的图片缩到长边不超过 1600 像素再转成 JPEG 字节——这个联调页只是拿来测接口通不通，
    /// 不需要留原图画质，缩小之后既不会撞上阿里云 4096×4096 的限制，也能让这个付费接口调用更快、更省。</summary>
    private const int MaxDimension = 1600;
    private static async Task<byte[]> ResizeIfNeededAsync(IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        using var image = await Image.LoadAsync(stream);
        image.Mutate(x => x.Resize(new ResizeOptions
        { Mode = ResizeMode.Max, Size = new Size(MaxDimension, MaxDimension) }));
        using var outStream = new MemoryStream();
        await image.SaveAsync(outStream, new JpegEncoder { Quality = 90 });
        return outStream.ToArray();
    }
}
