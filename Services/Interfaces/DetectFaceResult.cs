namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 人脸检测结果（不比对，只看这张图里"有没有脸、脸清不清楚"），用于录入参考照片时把关。
/// FaceCount：检测到几张脸；QualityScore：综合质量分（0-100，越高越好）；
/// FaceSizeRatio：脸部框最长边占图片最长边的比例（太小说明人离镜头太远/照片没对准脸）；
/// MaxPoseAngle：俯仰/侧转/歪头三个姿态角度里绝对值最大的一个（度），太大说明不是正脸。
/// </summary>
public record DetectFaceResult(int FaceCount, double QualityScore, double FaceSizeRatio, double MaxPoseAngle);
