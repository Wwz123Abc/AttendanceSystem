namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 一次人脸校验的结果。
/// IsLive=false 表示活体检测没通过（怀疑是照片/视频冒充），这时 IsMatch 恒为 false，不会再去比对。
/// IsLive=true 但 IsMatch=false 表示确认是真人，但和参考照片不是同一个人。
/// </summary>
public record FaceVerifyResult(bool IsLive, bool IsMatch, double Confidence, string? FailReason);

/// <summary>
/// 人脸检测结果（不比对，只看这张图里"有没有脸、脸清不清楚"），用于录入参考照片时把关。
/// FaceCount：检测到几张脸；QualityScore：综合质量分（0-100，越高越好）；
/// FaceSizeRatio：脸部框最长边占图片最长边的比例（太小说明人离镜头太远/照片没对准脸）；
/// MaxPoseAngle：俯仰/侧转/歪头三个姿态角度里绝对值最大的一个（度），太大说明不是正脸。
/// </summary>
public record DetectFaceResult(int FaceCount, double QualityScore, double FaceSizeRatio, double MaxPoseAngle);

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
