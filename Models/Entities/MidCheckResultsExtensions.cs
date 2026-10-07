using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>AttendanceRecord.MidCheckResults 这个存库字符串的解析/格式化。</summary>
public static class MidCheckResultsExtensions
{
    /// <summary>
    /// 把存库字符串解析成每一段的判定结果列表，解析不了的段直接跳过。
    /// 同一段时间窗口重复出现（历史上班次配置手滑存了两次一样的窗口，当时就冻结进了这条记录）会被
    /// 合并成一条，跟 <see cref="ShiftScheduleExtensions.ParseMidCheckWindows"/> 的去重口径保持一致，
    /// 避免"取最晚一段""排除最后一段"这些逻辑把同一段窗口误当成两段独立窗口、把整天工时收缩成 0。
    /// </summary>
    public static List<MidCheckWindowResult> ParseMidCheckResults(this string? encoded) =>
        (encoded ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseOne)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .GroupBy(r => (r.WindowStart, r.WindowEnd))
            .Select(g => g.FirstOrDefault(r => r.IsSatisfied, g.First()))
            .ToList();

    private static MidCheckWindowResult? ParseOne(string seg)
    {
        var eq = seg.Split('=');
        if (eq.Length != 2) return null;
        var range = eq[0].Split('-');
        if (range.Length != 2 || !TimeOnly.TryParse(range[0], out var s) || !TimeOnly.TryParse(range[1], out var e)) return null;
        return new MidCheckWindowResult(s, e, TimeOnly.TryParse(eq[1], out var h) ? h : null);
    }

    /// <summary>把判定结果列表格式化回存库用的字符串。</summary>
    public static string? FormatMidCheckResults(this List<MidCheckWindowResult> results) =>
        results.Count == 0 ? null : string.Join(";", results.Select(r =>
            $"{r.WindowStart:HH\\:mm}-{r.WindowEnd:HH\\:mm}={(r.HitTime.HasValue ? r.HitTime.Value.ToString("HH\\:mm") : "")}"));
}
