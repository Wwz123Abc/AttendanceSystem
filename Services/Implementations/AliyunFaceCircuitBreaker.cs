using AlibabaCloud.SDK.Facebody20191230;
using AlibabaCloud.SDK.Facebody20191230.Models;
using AlibabaCloud.TeaUtil.Models;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;
using SixLabors.ImageSharp;
using Tea;

namespace AttendanceSystem.Services.Implementations;

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
