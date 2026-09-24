using System.Diagnostics;
using AlibabaCloud.SDK.Facebody20191230;
using AlibabaCloud.SDK.Facebody20191230.Models;
using AlibabaCloud.TeaUtil.Models;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;
using SixLabors.ImageSharp;
using Tea;

namespace AttendanceSystem.Services.Implementations;

/// <summary>阿里云人脸识别接口调用失败（网络/签名/服务端错误），消息已经是给管理员/日志看的中文说明。</summary>
public class AliyunFaceApiException(string message) : Exception(message);

/// <summary>
/// 阿里云"视觉智能开放平台"（Facebody）人脸识别客户端：先活体检测、再 1:1 人脸比对。
/// 两步都调用官方 SDK 的 XxxAdvance 方法，直接传内存里的图片字节流，不用先传到 OSS。
///
/// 带了三层防护，避免阿里云那边一抖动就把整个远程打卡拖垮：
/// ① 超时——RuntimeOptions 配了连接/读取超时，卡住最多等这么久就放弃，不会无限期占住请求线程；
/// ② 重试——只对"网络类瞬时错误"重试一次（签名错、参数错、识别没通过这些重试没意义，直接放弃）；
/// ③ 熔断——连续失败次数过多时（比如阿里云那边整体故障），后续请求直接快速失败一段时间，
///    不再真的去调用（付费）接口，避免所有远程打卡请求排队等超时，把网站拖慢。
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
        if (AliyunFaceCircuitBreaker.IsOpen)
            throw new AliyunFaceApiException("人脸识别服务当前调用异常次数过多，已暂时熔断保护，请稍后再试");

        // 活体+比对是串行两次调用，每次内部还可能重试——不加个总预算的话，最坏情况能拖到近一分钟，
        // 页面卡住太久。这里给"这一整次 VerifyAsync"设一个总预算，超了直接按失败处理，不会无限等下去。
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(_opt.OverallBudgetMs);
        var opCt = budgetCts.Token;

        var client  = CreateClient();
        var runtime = new RuntimeOptions
        {
            ConnectTimeout = _opt.ConnectTimeoutMs,
            ReadTimeout    = _opt.ReadTimeoutMs,
        };

        // ── 第一步：活体检测——拍到的是不是一个真人现场拍摄的（防止拿照片/视频冒充打卡）──
        bool isLive;
        try
        {
            using var liveStream = new MemoryStream(liveImage);
            var sw   = Stopwatch.StartNew();
            var resp = await ExecuteWithRetryAsync(
                () =>
                {
                    var task = new DetectLivingFaceAdvanceRequest.DetectLivingFaceAdvanceRequestTasks { ImageURLObject = liveStream };
                    var req  = new DetectLivingFaceAdvanceRequest { Tasks = [task] };
                    return client.DetectLivingFaceAdvanceAsync(req, runtime);
                },
                [liveStream], ct, opCt);
            sw.Stop();

            var suggestion = resp.Body?.Data?.Elements?.FirstOrDefault()?.Results?.FirstOrDefault()?.Suggestion;
            isLive = string.Equals(suggestion, "pass", StringComparison.OrdinalIgnoreCase);
            logger.LogInformation("活体检测调用完成，耗时 {ElapsedMs}ms，Suggestion={Suggestion}，RequestId={RequestId}",
                sw.ElapsedMilliseconds, suggestion ?? "(空)", resp.Body?.RequestId);
            // 这里故意不调 RecordSuccess：活体→比对是串行两步，如果活体这步成功就把连续失败计数清零，
            // 那么"只有比对接口在故障"时，每个请求都是"清零 + 记 1 次失败"，计数永远到不了阈值，熔断
            // 就等于失效了。只在整次 VerifyAsync 都成功（比对也成功，见下面）才清零（2026-09-24 审查修复）。
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 是调用方（HTTP 请求）自己取消的，不算阿里云失败，不计熔断、不包装
        }
        catch (OperationCanceledException)
        {
            // 走到这里说明不是调用方取消的，是总预算超时——单独处理，避免用户看到英文的
            // "The operation was canceled." 这种技术味很重的提示，日志里也能跟"接口真的报错"区分开。
            RecordFailureAndLogIfBreakerJustOpened("活体检测");
            logger.LogWarning("人脸识别总预算 {BudgetMs}ms 超时（活体检测阶段）", _opt.OverallBudgetMs);
            throw new AliyunFaceApiException("人脸识别服务响应超时，请稍后重试，或改用补卡申请");
        }
        catch (Exception ex)
        {
            if (ShouldCountForCircuitBreaker(ex)) RecordFailureAndLogIfBreakerJustOpened("活体检测");
            logger.LogWarning(ex, "活体检测接口调用失败");
            // 不把 ex.Message 拼进抛给上层/最终展示给员工的提示里——那是阿里云 SDK 原始的报错文本，
            // 可能带内部域名/请求 ID 等技术细节，不该让普通员工看到；完整异常已经记进上面的日志，
            // 需要排查时看日志就够了。
            throw new AliyunFaceApiException("活体检测接口调用失败，请稍后重试");
        }

        if (!isLive)
            return new FaceVerifyResult(false, false, 0, "未检测到真实人脸，请正对摄像头、保证光线充足后重试");

        // ── 第二步：1:1 人脸比对——和员工录入的参考照片是不是同一个人 ──
        double confidence;
        try
        {
            using var refStream  = new MemoryStream(referenceImage);
            using var liveStream = new MemoryStream(liveImage);
            var sw   = Stopwatch.StartNew();
            var resp = await ExecuteWithRetryAsync(
                () =>
                {
                    var req = new CompareFaceAdvanceRequest { ImageURLAObject = refStream, ImageURLBObject = liveStream };
                    return client.CompareFaceAdvanceAsync(req, runtime);
                },
                [refStream, liveStream], ct, opCt);
            sw.Stop();

            confidence = resp.Body?.Data?.Confidence ?? 0;
            logger.LogInformation("人脸比对调用完成，耗时 {ElapsedMs}ms，Confidence={Confidence}，RequestId={RequestId}",
                sw.ElapsedMilliseconds, confidence, resp.Body?.RequestId);
            AliyunFaceCircuitBreaker.RecordSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            RecordFailureAndLogIfBreakerJustOpened("人脸比对");
            logger.LogWarning("人脸识别总预算 {BudgetMs}ms 超时（人脸比对阶段）", _opt.OverallBudgetMs);
            throw new AliyunFaceApiException("人脸识别服务响应超时，请稍后重试，或改用补卡申请");
        }
        catch (Exception ex)
        {
            if (ShouldCountForCircuitBreaker(ex)) RecordFailureAndLogIfBreakerJustOpened("人脸比对");
            logger.LogWarning(ex, "人脸比对接口调用失败");
            throw new AliyunFaceApiException("人脸比对接口调用失败，请稍后重试");
        }

        var isMatch = confidence >= _opt.MatchThreshold;
        return new FaceVerifyResult(true, isMatch, confidence,
            isMatch ? null : $"人脸识别未通过（相似度 {confidence:F0}，需要 {_opt.MatchThreshold:F0} 以上），请正对摄像头重试");
    }

    public async Task<DetectFaceResult> DetectFaceAsync(byte[] image, CancellationToken ct = default)
    {
        if (AliyunFaceCircuitBreaker.IsOpen)
            throw new AliyunFaceApiException("人脸识别服务当前调用异常次数过多，已暂时熔断保护，请稍后再试");

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(_opt.OverallBudgetMs);
        var opCt = budgetCts.Token;

        var client  = CreateClient();
        var runtime = new RuntimeOptions
        {
            ConnectTimeout = _opt.ConnectTimeoutMs,
            ReadTimeout    = _opt.ReadTimeoutMs,
        };

        try
        {
            using var stream = new MemoryStream(image);
            var sw   = Stopwatch.StartNew();
            var resp = await ExecuteWithRetryAsync(
                () =>
                {
                    var req = new DetectFaceAdvanceRequest
                    {
                        ImageURLObject = stream,
                        Quality        = true,
                        Pose           = true,   // 拿姿态角度，用来挡侧脸/歪头——质量分主要看清晰度/光照，侧脸也可能拿到高分
                        Landmark       = false,
                        MaxFaceNumber  = 5,
                    };
                    return client.DetectFaceAdvanceAsync(req, runtime);
                },
                [stream], ct, opCt);
            sw.Stop();
            AliyunFaceCircuitBreaker.RecordSuccess();

            var data      = resp.Body?.Data;
            var faceCount = data?.FaceCount ?? 0;
            double qualityScore = 0, sizeRatio = 0, maxPoseAngle = 0;
            if (faceCount == 1)
            {
                qualityScore = data!.Qualities?.ScoreList?.FirstOrDefault() ?? 0;
                var rect = data.FaceRectangles;   // [left, top, width, height]（只有一张脸，取前 4 个）
                if (rect is { Count: >= 4 })
                {
                    using var img = Image.Load(image);
                    var faceLongSide  = Math.Max(rect[2] ?? 0, rect[3] ?? 0);
                    var imageLongSide = Math.Max(img.Width, img.Height);
                    sizeRatio = imageLongSide > 0 ? (double)faceLongSide / imageLongSide : 0;
                }
                // PoseList 是 [Pitch, Roll, Yaw]（阿里云返回顺序），这里不逐个较真具体哪个是哪个轴，
                // 直接取三个角度里绝对值最大的一个——任何一个轴转得太多都说明不是正脸，够用也更保险
                // （猜错索引顺序的风险，比"统一按最大值卡阈值"更大）。
                if (data.PoseList is { Count: > 0 })
                    maxPoseAngle = data.PoseList.Where(v => v.HasValue).Select(v => Math.Abs(v!.Value)).DefaultIfEmpty(0).Max();
            }
            logger.LogInformation("人脸检测调用完成，耗时 {ElapsedMs}ms，FaceCount={FaceCount}，Quality={Quality}，SizeRatio={SizeRatio:F2}，MaxPoseAngle={MaxPoseAngle:F1}，RequestId={RequestId}",
                sw.ElapsedMilliseconds, faceCount, qualityScore, sizeRatio, maxPoseAngle, resp.Body?.RequestId);

            return new DetectFaceResult(faceCount, qualityScore, sizeRatio, maxPoseAngle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            RecordFailureAndLogIfBreakerJustOpened("人脸检测");
            logger.LogWarning("人脸识别总预算 {BudgetMs}ms 超时（人脸检测）", _opt.OverallBudgetMs);
            throw new AliyunFaceApiException("人脸识别服务响应超时，请稍后重试");
        }
        catch (Exception ex)
        {
            if (ShouldCountForCircuitBreaker(ex)) RecordFailureAndLogIfBreakerJustOpened("人脸检测");
            logger.LogWarning(ex, "人脸检测接口调用失败");
            throw new AliyunFaceApiException("人脸检测接口调用失败，请稍后重试");
        }
    }

    /// <summary>只有"服务端/网络类"的失败才计入熔断（5xx、限流、超时、连接失败）；4xx 参数错误
    /// （比如某个员工传了畸形/超大图片）说明是这一次请求本身有问题，跟阿里云服务是否健康无关，
    /// 不该被算进熔断计数——否则单个员工连续传错几次图，就能把全公司的远程打卡熔断掉。</summary>
    private static bool ShouldCountForCircuitBreaker(Exception ex) => ex switch
    {
        TeaException te => te.StatusCode >= 500
                         || (te.Code?.Contains("Throttling", StringComparison.OrdinalIgnoreCase) ?? false),
        TaskCanceledException                => true,
        TimeoutException                     => true,
        System.Net.Http.HttpRequestException => true,
        System.Net.Sockets.SocketException   => true,
        _                                     => false
    };

    /// <summary>记一次熔断失败；如果这一下正好把熔断打开了，额外用 LogError 记一条——熔断打开意味着
    /// "接下来一段时间所有人都用不了远程打卡"，这种程度的问题不该跟普通的单次调用失败一样只是 Warning。</summary>
    private void RecordFailureAndLogIfBreakerJustOpened(string stage)
    {
        var justOpened = AliyunFaceCircuitBreaker.RecordFailure(_opt.CircuitBreakerFailureThreshold, _opt.CircuitBreakerOpenSeconds);
        if (justOpened)
            logger.LogError("阿里云人脸识别连续失败达到阈值（{Threshold} 次），已熔断 {OpenSeconds} 秒，期间所有远程打卡的人脸识别请求会直接失败。触发阶段：{Stage}",
                _opt.CircuitBreakerFailureThreshold, _opt.CircuitBreakerOpenSeconds, stage);
    }

    /// <summary>
    /// 只对"网络类瞬时错误"重试（超时、连接失败、5xx、限流），最多重试 MaxRetryAttempts 次；
    /// 签名错/参数错这类"重试也没用还多花一次调用钱"的错误，第一次失败就直接抛出去，不重试。
    /// 重试前会把用到的 MemoryStream 位置重置到开头——第一次尝试可能已经把流读到末尾了。
    /// callerCt 是调用方原始的取消令牌（比如 HTTP 请求被中断），opCt 是叠加了"总预算超时"之后
    /// 实际用来等待/取消的令牌——两个分开传，是为了在 IsRetryable 里能分清"是调用方主动取消的
    /// （这种直接放弃、不重试），还是单纯的超时（这种才值得重试）。
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, MemoryStream[] streamsToReset, CancellationToken callerCt, CancellationToken opCt)
    {
        for (var attempt = 0; ; attempt++)
        {
            opCt.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                foreach (var s in streamsToReset) s.Position = 0;
                logger.LogInformation("阿里云人脸识别接口第 {Attempt} 次重试", attempt + 1);
                await Task.Delay(_opt.RetryBackoffMs, opCt);
            }
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < _opt.MaxRetryAttempts && IsRetryable(ex, callerCt))
            {
                logger.LogWarning(ex, "阿里云人脸识别接口调用失败（第 {Attempt} 次尝试），判定为瞬时错误，准备重试", attempt + 1);
            }
        }
    }

    /// <summary>callerCt 是调用方原始的取消令牌：如果调用方已经主动取消了（比如员工关掉了页面），
    /// 就算 SDK 抛的是"看起来能重试"的异常也不该再重试——没人等着这个结果了，重试只是白花调用量。</summary>
    private static bool IsRetryable(Exception ex, CancellationToken callerCt)
    {
        if (callerCt.IsCancellationRequested) return false;

        return ex switch
        {
            // "图片无法下载"：SDK 的 XxxAdvance 方法内部会先把图片传到阿里云自己的临时 OSS 再让识别服务去读，
            // 这一步偶尔会失败（生产实测确实是转瞬即逝——同一张图隔几秒重试就通过），值得重试一次，
            // 不是图片内容本身有问题（那种情况阿里云会报别的更具体的错，不会是这条）。
            TeaException te => te.StatusCode >= 500
                             || (te.Code?.Contains("Throttling", StringComparison.OrdinalIgnoreCase) ?? false)
                             || (te.Message?.Contains("图片无法下载") ?? false),
            TaskCanceledException      => true,   // ReadTimeout/ConnectTimeout 触发（callerCt 没取消，走到这就是纯超时）
            TimeoutException           => true,
            System.Net.Http.HttpRequestException => true,
            System.Net.Sockets.SocketException   => true,
            _                          => false
        };
    }
}

/// <summary>
/// 简单的进程内熔断器：连续失败达到阈值就打开一段时间，期间所有调用直接快速失败，
/// 不再真的请求阿里云接口。静态字段是有意的——AliyunFaceClient 是按请求 Scoped 注册的，
/// 熔断状态必须跨请求共享才有意义。
/// </summary>
internal static class AliyunFaceCircuitBreaker
{
    private static int  _consecutiveFailures;
    private static long _openUntilTicks;

    public static bool IsOpen => Environment.TickCount64 < Interlocked.Read(ref _openUntilTicks);

    public static void RecordSuccess() => Interlocked.Exchange(ref _consecutiveFailures, 0);

    /// <summary>返回这一次失败是否正好把熔断打开了（供调用方决定要不要额外记一条更高级别的日志）。</summary>
    public static bool RecordFailure(int threshold, int openSeconds)
    {
        if (Interlocked.Increment(ref _consecutiveFailures) < threshold) return false;
        Interlocked.Exchange(ref _openUntilTicks, Environment.TickCount64 + openSeconds * 1000L);
        Interlocked.Exchange(ref _consecutiveFailures, 0);   // 熔断打开后重新计数，避免打开期间还在累加
        return true;
    }
}
