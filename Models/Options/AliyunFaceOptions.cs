namespace AttendanceSystem.Models.Options;

/// <summary>
/// 阿里云"视觉智能开放平台"人脸识别配置：打卡时先做活体检测（防止拿照片/视频冒充），
/// 再做 1:1 人脸比对（和员工录入的参考照片是不是同一个人）。
/// </summary>
public class AliyunFaceOptions
{
    public const string SectionName = "AliyunFace";

    public string AccessKeyId     { get; set; } = string.Empty;
    public string AccessKeySecret { get; set; } = string.Empty;

    /// <summary>接口地域节点，人脸类接口目前只在上海开放。</summary>
    public string Endpoint { get; set; } = "facebody.cn-shanghai.aliyuncs.com";

    /// <summary>人脸比对相似度阈值（0-100），达到这个分数才算同一个人，越高越严格。</summary>
    public double MatchThreshold { get; set; } = 80;

    /// <summary>同一用户在时间窗口内最多允许失败几次（超过就临时锁一下，防止有人拿别人照片反复试/刷调用量）。</summary>
    public int MaxAttemptsPerWindow { get; set; } = 5;

    /// <summary>失败次数统计的时间窗口（分钟）。</summary>
    public int AttemptWindowMinutes { get; set; } = 10;
}
