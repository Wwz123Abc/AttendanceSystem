using System.Collections.Concurrent;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 管理员助手限流器（进程内内存实现，单机部署够用）：
/// 每个用户"每分钟次数 + 每天次数"双上限，超了就拒绝并给出剩余等待。
/// 说明：纯内存计数在应用重启后会清零——对防滥用足够（重启后重新累积），
/// 如需跨重启持久化可换数据库实现，本期不做。
/// </summary>
public static class AgentRateLimiter
{
    // userId → 最近请求时刻队列（滑动 1 分钟窗口）
    private static readonly ConcurrentDictionary<int, Queue<DateTime>> MinuteWindows = new();

    // (userId, 天) → 当天请求数
    private static readonly ConcurrentDictionary<(int UserId, DateOnly Day), int> DailyCounts = new();

    /// <summary>
    /// 尝试放行一次请求。
    /// </summary>
    /// <returns>true=放行；false=被限流，reason 说明是分钟还是天上限。</returns>
    public static bool TryConsume(int userId, int perMinute, int dailyLimit, out string reason)
    {
        reason = string.Empty;
        if (userId <= 0) return false;

        var now   = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        // 天上限（先查再记，允许轻微超放）
        var dailyKey = (userId, today);
        var daily = DailyCounts.GetOrAdd(dailyKey, 0);
        if (daily >= dailyLimit)
        {
            reason = $"今日对话次数已达上限（{dailyLimit} 次），请明天再试";
            return false;
        }

        // 分钟窗口：清掉超过 1 分钟的记录
        var q = MinuteWindows.GetOrAdd(userId, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && (now - q.Peek()).TotalMinutes >= 1)
                q.Dequeue();

            if (q.Count >= perMinute)
            {
                var wait = 60 - (int)(now - q.Peek()).TotalSeconds;
                reason = $"操作太频繁，请 {Math.Max(wait, 1)} 秒后再试";
                return false;
            }

            q.Enqueue(now);
        }

        DailyCounts.AddOrUpdate(dailyKey, 1, (_, c) => c + 1);
        return true;
    }

    /// <summary>清理当天以前的天计数（防字典无限增长；可每天定时调一次，也可不管——量级很小）。</summary>
    public static void CleanupExpired()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var key in DailyCounts.Keys)
            if (key.Day < today)
                DailyCounts.TryRemove(key, out _);
    }
}
