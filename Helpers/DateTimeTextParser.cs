using System.Globalization;

namespace AttendanceSystem.Helpers;

/// <summary>
/// 日期/时间文本的容错解析（2026-11-20）：移动端不再依赖浏览器的日期/时间控件，
/// 允许员工直接输入 2026-11-16、20:30、2026-11-16 20:30，也兼容纯数字 20261116 / 2030 / 202611162030。
/// </summary>
public static class DateTimeTextParser
{
    /// <summary>解析日期+时间（返回的 DateTime 带具体时刻）。</summary>
    public static bool TryParseDateTime(string? text, out DateTime value)
    {
        value = default;
        var s = Normalize(text);
        if (s.Length == 0) return false;

        // 纯数字：202611162030（12 位）或 20261116（8 位，按当天 00:00）
        if (s.All(char.IsDigit))
        {
            if (s.Length == 12)
                s = $"{s[..4]}-{s[4..6]}-{s[6..8]} {s[8..10]}:{s[10..12]}";
            else if (s.Length == 8)
                s = $"{s[..4]}-{s[4..6]}-{s[6..8]} 00:00";
        }

        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>解析日期（只要年月日）。</summary>
    public static bool TryParseDate(string? text, out DateOnly value)
    {
        value = default;
        var s = Normalize(text);
        if (s.Length == 0) return false;
        if (s.All(char.IsDigit) && s.Length == 8)
            s = $"{s[..4]}-{s[4..6]}-{s[6..8]}";
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return false;
        value = DateOnly.FromDateTime(dt);
        return true;
    }

    /// <summary>解析时间（HH:mm），兼容 2030 / 930 这种纯数字输入。</summary>
    public static bool TryParseTime(string? text, out TimeOnly value)
    {
        value = default;
        var s = Normalize(text);
        if (s.Length == 0) return false;
        if (s.All(char.IsDigit))
        {
            if (s.Length == 3) s = $"0{s[0]}:{s[1..]}";
            else if (s.Length == 4) s = $"{s[..2]}:{s[2..]}";
        }
        return TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>把全角冒号/斜杠/多余空格统一成标准写法，便于后面解析。</summary>
    private static string Normalize(string? text) => (text ?? string.Empty)
        .Trim()
        .Replace('：', ':')
        .Replace('/', '-')
        .Replace('T', ' ')
        .Replace("  ", " ");
}
