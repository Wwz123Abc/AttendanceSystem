using AlibabaCloud.SDK.Facebody20191230;
using AlibabaCloud.SDK.Facebody20191230.Models;
using AlibabaCloud.TeaUtil.Models;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>阿里云人脸识别接口调用失败（网络/签名/服务端错误），消息已经是给管理员/日志看的中文说明。</summary>
public class AliyunFaceApiException(string message) : Exception(message);

/// <summary>
/// 阿里云"视觉智能开放平台"（Facebody）人脸识别客户端：先活体检测、再 1:1 人脸比对。
/// 两步都调用官方 SDK 的 XxxAdvance 方法，直接传内存里的图片字节流，不用先传到 OSS。
/// </summary>
public class AliyunFaceClient(IOptions<AliyunFaceOptions> options, ILogger<AliyunFaceClient> logger) : IAliyunFaceClient
{
    private readonly AliyunFaceOptions _opt = options.Value;

    private Client CreateClient()
    {
        if (string.IsNullOrWhiteSpace(_opt.AccessKeyId) || string.IsNullOrWhiteSpace(_opt.AccessKeySecret))
            throw new InvalidOperationException("未配置阿里云人脸识别 AccessKeyId/AccessKeySecret（appsettings.json 的 AliyunFace 节）");

        return new Client(new AlibabaCloud.OpenApiClient.Models.Config
        {
            AccessKeyId     = _opt.AccessKeyId,
            AccessKeySecret = _opt.AccessKeySecret,
            Endpoint        = _opt.Endpoint,
        });
    }

    public async Task<FaceVerifyResult> VerifyAsync(byte[] referenceImage, byte[] liveImage, CancellationToken ct = default)
    {
        var client  = CreateClient();
        var runtime = new RuntimeOptions();

        // ── 第一步：活体检测——拍到的是不是一个真人现场拍摄的（防止拿照片/视频冒充打卡）──
        bool isLive;
        try
        {
            using var liveStream = new MemoryStream(liveImage);
            var task = new DetectLivingFaceAdvanceRequest.DetectLivingFaceAdvanceRequestTasks { ImageURLObject = liveStream };
            var req  = new DetectLivingFaceAdvanceRequest { Tasks = [task] };
            var resp = await client.DetectLivingFaceAdvanceAsync(req, runtime);

            var suggestion = resp.Body?.Data?.Elements?.FirstOrDefault()?.Results?.FirstOrDefault()?.Suggestion;
            isLive = string.Equals(suggestion, "pass", StringComparison.OrdinalIgnoreCase);
            if (!isLive)
                logger.LogInformation("人脸活体检测未通过，Suggestion={Suggestion}", suggestion ?? "(空)");
        }
        catch (Exception ex)
        {
            throw new AliyunFaceApiException("活体检测接口调用失败：" + ex.Message);
        }

        if (!isLive)
            return new FaceVerifyResult(false, false, 0, "未检测到真实人脸，请正对摄像头、保证光线充足后重试");

        // ── 第二步：1:1 人脸比对——和员工录入的参考照片是不是同一个人 ──
        double confidence;
        try
        {
            using var refStream  = new MemoryStream(referenceImage);
            using var liveStream = new MemoryStream(liveImage);
            var req  = new CompareFaceAdvanceRequest { ImageURLAObject = refStream, ImageURLBObject = liveStream };
            var resp = await client.CompareFaceAdvanceAsync(req, runtime);
            confidence = resp.Body?.Data?.Confidence ?? 0;
        }
        catch (Exception ex)
        {
            throw new AliyunFaceApiException("人脸比对接口调用失败：" + ex.Message);
        }

        var isMatch = confidence >= _opt.MatchThreshold;
        return new FaceVerifyResult(true, isMatch, confidence,
            isMatch ? null : $"人脸识别未通过（相似度 {confidence:F0}，需要 {_opt.MatchThreshold:F0} 以上），请正对摄像头重试");
    }
}
