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

    /// <summary>调用阿里云接口的连接超时（毫秒）。</summary>
    public int ConnectTimeoutMs { get; set; } = 3000;

    /// <summary>调用阿里云接口的读取超时（毫秒），网络/服务端卡住时最多等这么久就放弃。</summary>
    public int ReadTimeoutMs { get; set; } = 5000;

    /// <summary>网络类瞬时错误最多重试几次（不含首次尝试；签名错/参数错/识别不通过这些不算瞬时错误，不重试）。
    /// 生产实测"图片无法下载"这类阿里云内部临时 OSS 中转失败，重试 1 次不一定够，给到 2 次。</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>重试前的退避等待（毫秒）。</summary>
    public int RetryBackoffMs { get; set; } = 500;

    /// <summary>一次 VerifyAsync/DetectFaceAsync 调用（含重试）总共最多花多久（毫秒）——单次调用按
    /// 连接+读取超时乘以重试次数算，最坏能到二十多秒，VerifyAsync 里活体+比对还是串行两次调用，
    /// 不加个总预算的话最坏能拖到近一分钟，页面卡住太久、体验很差。超过这个预算直接按失败处理
    /// （计入熔断、给员工正常的"服务暂时不可用"提示），不会无限期等下去。</summary>
    public int OverallBudgetMs { get; set; } = 12000;

    /// <summary>连续失败多少次之后熔断（暂停调用阿里云接口一段时间，避免服务整体故障时拖垮全站）。</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>熔断打开后维持多少秒，期间直接快速失败，不再真的调用阿里云接口。</summary>
    public int CircuitBreakerOpenSeconds { get; set; } = 60;

    /// <summary>录入参考照片时，人脸质量分（0-100）低于这个值就拒绝保存。实测一张正常清晰的正脸照
    /// 综合质量分接近 100，这里给得比较宽松，只挡明显不合格的照片（模糊/逆光/遮挡严重等）。</summary>
    public double EnrollMinQualityScore { get; set; } = 50;

    /// <summary>录入参考照片时，人脸框最长边占图片最长边的比例，低于这个值说明人离镜头太远/照片没对准脸。</summary>
    public double EnrollMinFaceSizeRatio { get; set; } = 0.15;

    /// <summary>录入参考照片时，俯仰/侧转/歪头三个姿态角度（度）里最大的一个不能超过这个值，
    /// 超了说明是侧脸/歪头——质量分主要看清晰度光照，侧脸也可能拿高分，得单独卡这一项。</summary>
    public double EnrollMaxPoseAngle { get; set; } = 20;

    // ── 以下是"成本闸门"：跟前面 MaxAttemptsPerWindow（失败限流，防止拿别人照片反复试）是两回事——
    // 这里挡的是"手快连点"造成的重复付费调用，不是防冒充。命中时会在真正调用（付费的）阿里云接口
    // 之前就拦下来，且不计入失败限流，避免"被这里拦 → 算一次失败 → 更容易触发失败限流"的连锁反应。
    // （每日次数上限已于 2026-09-21 按业务要求取消，人脸识别打卡不再限制每人每天的次数。）

    /// <summary>两次成功识别之间最少要隔多少秒，防止手快连点/网络重试造成重复的付费调用。</summary>
    public int MinSecondsBetweenVerifications { get; set; } = 30;
}
