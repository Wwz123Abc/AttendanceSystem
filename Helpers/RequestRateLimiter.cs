using System.Collections.Concurrent;

namespace AttendanceSystem.Helpers;

/// <summary>
/// 极简的"按用户/窗口"请求限流（进程内内存，单实例部署够用）：
/// 比如远程打卡页在调用（付费的）人脸识别之前先用它挡掉高频请求。
/// 为什么不用账号失败限流代替：失败限流统计的是"识别失败"，而被成本闸门、参数校验、
/// 服务异常挡下的请求根本不会产生失败记录，光靠它挡不住"疯狂点按钮/脚本刷接口"（2026-11-16 复审 P2）。
/// 用固定窗口 + 队列实现：窗口内请求数超过上限就拒绝，并告诉调用方还要等多少秒。
/// </summary>
public static class RequestRateLimiter
{
    // key（例如 "RemotePunch:用户Id"）→ 该 key 最近窗口内的请求时间，按时间升序
    private static readonly ConcurrentDictionary<string, Queue<DateTime>> Buckets = new();

    /// <summary>
    /// 尝试占用一次配额：窗口内已到上限返回 false，并通过 retryAfter 给出还需等待多久。
    /// 时间由调用方传入（便于单测）；生产调用传 DateTime.Now 即可。
    /// </summary>
    public static bool TryAcquire(string key, int maxRequests, TimeSpan window, DateTime now, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (maxRequests <= 0 || window <= TimeSpan.Zero) return true;   // 配置为 0 表示不限制

        var bucket = Buckets.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (bucket)
        {
            // 丢掉窗口外的旧记录，避免队列无限增长（内存上限 = 每个 key 最多 maxRequests 条）
            while (bucket.Count > 0 && now - bucket.Peek() >= window) bucket.Dequeue();

            if (bucket.Count >= maxRequests)
            {
                retryAfter = bucket.Peek() + window - now;
                if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
                return false;
            }

            bucket.Enqueue(now);
            return true;
        }
    }

    /// <summary>测试用：清空所有窗口记录。</summary>
    public static void Reset() => Buckets.Clear();
}
