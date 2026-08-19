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
