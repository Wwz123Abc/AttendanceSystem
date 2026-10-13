using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Helpers;

/// <summary>
/// "不算钱"的饭点时段（工作/加班/请假区间压到这几段，重叠的部分不计入时长），<b>按考勤组各设各的</b>。
/// 默认（没单独设置过的考勤组）是 12:00-13:00、17:30-18:00、00:00-01:00、05:30-06:00（2026-09-29 用户确认）；
/// 2026-10-13 起总部管理员可以在"饭点设置"页给某个考勤组单独改，保存在 <see cref="SystemSetting"/> 表里
/// （键 <c>UnpaidBreakWindows:考勤组编号</c>，没有这一行的考勤组用默认值）；没有考勤组的人也用默认值。
/// 工时公式 <c>AttendanceService.ComputeWorkHours</c> 是静态方法、到处在调，所以当前生效的设置放在这里的静态变量里：
/// 程序启动时从库里读一次（<see cref="LoadAsync"/>），管理员保存后立刻换成新的。只有一个网站实例，不用考虑多实例同步。
/// </summary>
public static class UnpaidBreakSettings
{
    public const string KeyPrefix = "UnpaidBreakWindows:";
    public const int MaxWindows = 8;
    public const int MaxWindowMinutes = 240;

    public static readonly IReadOnlyList<(TimeOnly Start, TimeOnly End)> Defaults =
    [
        (new TimeOnly(0, 0), new TimeOnly(1, 0)),
        (new TimeOnly(5, 30), new TimeOnly(6, 0)),
        (new TimeOnly(12, 0), new TimeOnly(13, 0)),
        (new TimeOnly(17, 30), new TimeOnly(18, 0)),
    ];

    private static volatile IReadOnlyDictionary<int, (TimeOnly Start, TimeOnly End)[]> _overrides
        = new Dictionary<int, (TimeOnly, TimeOnly)[]>();

    public static string KeyFor(int groupId) => KeyPrefix + groupId;

    /// <summary>某个考勤组当前生效的饭点时段（按开始时间排好序）；<paramref name="groupId"/> 为空或没单独设置过就是默认。</summary>
    public static IReadOnlyList<(TimeOnly Start, TimeOnly End)> WindowsFor(int? groupId)
    {
        if (groupId is { } g && _overrides.TryGetValue(g, out var w)) return w;
        return Defaults;
    }

    /// <summary>这个考勤组有没有单独设置过。</summary>
    public static bool IsCustomized(int groupId) => _overrides.ContainsKey(groupId);

    /// <summary>给某个考勤组换成新的时段（调用前先用 <see cref="Validate"/> 校验过）。和默认一样的话等于"没单独设置"。</summary>
    public static void Apply(int groupId, IEnumerable<(TimeOnly Start, TimeOnly End)> windows)
    {
        var sorted = windows.OrderBy(w => w.Start).ToArray();
        var next = new Dictionary<int, (TimeOnly, TimeOnly)[]>(_overrides);
        if (Format(sorted) == Format(Defaults)) next.Remove(groupId); else next[groupId] = sorted;
        _overrides = next;
    }

    /// <summary>这个考勤组恢复默认。</summary>
    public static void Remove(int groupId)
    {
        var next = new Dictionary<int, (TimeOnly, TimeOnly)[]>(_overrides);
        next.Remove(groupId);
        _overrides = next;
    }

    /// <summary>所有考勤组都恢复默认（测试用）。</summary>
    public static void ResetAll() => _overrides = new Dictionary<int, (TimeOnly, TimeOnly)[]>();

    /// <summary>"12:00-13:00,17:30-18:00" 这种存库格式。</summary>
    public static string Format(IEnumerable<(TimeOnly Start, TimeOnly End)> windows)
        => string.Join(",", windows.OrderBy(w => w.Start).Select(w => $"{w.Start:HH\\:mm}-{w.End:HH\\:mm}"));

    /// <summary>解析存库格式；格式不对返回 null。空字符串表示"没有饭点时段"，返回空列表。</summary>
    public static List<(TimeOnly Start, TimeOnly End)>? TryParse(string? text)
    {
        var list = new List<(TimeOnly, TimeOnly)>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var se = part.Split('-', StringSplitOptions.TrimEntries);
            if (se.Length != 2 || !TimeOnly.TryParse(se[0], out var s) || !TimeOnly.TryParse(se[1], out var e)) return null;
            list.Add((s, e));
        }
        return list;
    }

    /// <summary>校验管理员填的时段；有问题返回提示语，没问题返回 null。</summary>
    public static string? Validate(IReadOnlyList<(TimeOnly Start, TimeOnly End)> windows)
    {
        if (windows.Count > MaxWindows) return $"最多设置 {MaxWindows} 段饭点时段";
        foreach (var (s, e) in windows)
        {
            if (e <= s) return $"{s:HH\\:mm}-{e:HH\\:mm}：结束时间必须晚于开始时间（一段饭点不能跨过 0 点，跨 0 点的请拆成两段，如 23:30 到次日 00:30 拆成 23:30-23:59 和 00:00-00:30）";
            if ((e - s).TotalMinutes > MaxWindowMinutes) return $"{s:HH\\:mm}-{e:HH\\:mm}：一段饭点最长 {MaxWindowMinutes / 60} 小时，请检查";
        }
        var sorted = windows.OrderBy(w => w.Start).ToList();
        for (var i = 1; i < sorted.Count; i++)
            if (sorted[i].Start < sorted[i - 1].End)
                return $"{sorted[i - 1].Start:HH\\:mm}-{sorted[i - 1].End:HH\\:mm} 和 {sorted[i].Start:HH\\:mm}-{sorted[i].End:HH\\:mm} 有重叠，请调整";
        return null;
    }

    /// <summary>给页面提示用的一句话，如"12:00-13:00、17:30-18:00"；没有任何时段时返回"（没有设置饭点时段）"。</summary>
    public static string Describe(int? groupId = null)
    {
        var w = WindowsFor(groupId);
        return w.Count == 0 ? "（没有设置饭点时段）" : string.Join("、", w.OrderBy(x => x.Start).Select(x => $"{x.Start:HH\\:mm}-{x.End:HH\\:mm}"));
    }

    /// <summary>
    /// 程序启动时从库里读出所有考勤组的单独设置换上；表还没建好（还没跑迁移）调用方自己兜底。
    /// 某一行读不懂或校验不过就跳过（那个考勤组用默认值），不能让工时算崩。
    /// </summary>
    public static async Task LoadAsync(AttendanceDbContext db, CancellationToken ct = default)
    {
        var rows = await db.SystemSettings.Where(s => s.Key.StartsWith(KeyPrefix)).ToListAsync(ct);
        var next = new Dictionary<int, (TimeOnly, TimeOnly)[]>();
        foreach (var row in rows)
        {
            if (!int.TryParse(row.Key[KeyPrefix.Length..], out var gid)) continue;
            var parsed = TryParse(row.Value);
            if (parsed is null || Validate(parsed) is not null) continue;
            if (Format(parsed) == Format(Defaults)) continue;
            next[gid] = [.. parsed.OrderBy(w => w.Start)];
        }
        _overrides = next;
    }
}
