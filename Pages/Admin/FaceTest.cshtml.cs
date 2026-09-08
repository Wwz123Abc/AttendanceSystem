using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Services.Interfaces;

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

            byte[] refBytes, liveBytes;
            using (var ms = new MemoryStream()) { await ReferencePhoto.CopyToAsync(ms); refBytes = ms.ToArray(); }
            using (var ms = new MemoryStream()) { await LivePhoto.CopyToAsync(ms); liveBytes = ms.ToArray(); }

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
}
