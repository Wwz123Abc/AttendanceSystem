namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 一次人脸校验的结果。
/// IsLive=false 表示活体检测没通过（怀疑是照片/视频冒充），这时 IsMatch 恒为 false，不会再去比对。
/// IsLive=true 但 IsMatch=false 表示确认是真人，但和参考照片不是同一个人。
/// </summary>
public record FaceVerifyResult(bool IsLive, bool IsMatch, double Confidence, string? FailReason);
