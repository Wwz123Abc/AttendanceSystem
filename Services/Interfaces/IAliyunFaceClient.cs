namespace AttendanceSystem.Services.Interfaces;

/// <summary>阿里云人脸识别客户端：活体检测 + 1:1 人脸比对 + 人脸质量检测。</summary>
public interface IAliyunFaceClient
{
    /// <summary>
    /// 校验"当前这张打卡拍到的脸"是不是"参考照片里的本人"，且是不是真人现场拍摄（不是举着照片/视频冒充）。
    /// </summary>
    Task<FaceVerifyResult> VerifyAsync(byte[] referenceImage, byte[] liveImage, CancellationToken ct = default);

    /// <summary>检测一张照片里的人脸情况（张数、质量、大小占比），用于录入参考照片时的质量把关，不做身份比对。</summary>
    Task<DetectFaceResult> DetectFaceAsync(byte[] image, CancellationToken ct = default);
}
