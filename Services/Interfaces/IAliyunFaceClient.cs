namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 一次人脸校验的结果。
/// IsLive=false 表示活体检测没通过（怀疑是照片/视频冒充），这时 IsMatch 恒为 false，不会再去比对。
/// IsLive=true 但 IsMatch=false 表示确认是真人，但和参考照片不是同一个人。
/// </summary>
public record FaceVerifyResult(bool IsLive, bool IsMatch, double Confidence, string? FailReason);

/// <summary>阿里云人脸识别客户端：活体检测 + 1:1 人脸比对。</summary>
public interface IAliyunFaceClient
{
    /// <summary>
    /// 校验"当前这张打卡拍到的脸"是不是"参考照片里的本人"，且是不是真人现场拍摄（不是举着照片/视频冒充）。
    /// </summary>
    Task<FaceVerifyResult> VerifyAsync(byte[] referenceImage, byte[] liveImage, CancellationToken ct = default);
}
