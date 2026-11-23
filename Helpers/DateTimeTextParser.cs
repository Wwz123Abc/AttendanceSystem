using System.Globalization;
using System.Text.RegularExpressions;

namespace AttendanceSystem.Helpers;

/// <summary>
/// 日期/时间文本的容错解析（2026-11-20，2026-11-23 补充）：
/// 移动端不再依赖浏览器的日期/时间控件，允许员工直接输入
/// 2026-11-16、20:30、2026-11-16 20:30，也兼容纯数字 20261116 / 2030 / 202611162030，
/// 以及没写年份的 11-16 20:30（按今年算）、只写时间的 20:30（结束时间用开始日期补）。
/// 全角数字/冒号/斜杠也会自动转成半角。
/// </summary>
public static class DateTimeTextParser
{
    /// <summary>解析日期+时间（返回的 DateTime 带具体时刻）。fallbackDate 用于"只填了时间"的结束时间。</summary>
    public static bool TryParseDateTime(string? text, out DateTime value, DateOnly? fallbackDate = null)
    {
        value = default;
        var s = Normalize(text);
        if (s.Length == 0) return false;

        // 只写了时间（20:30 / 2030）：结束时间用开始日期补
        if (Regex.IsMatch(s, @"^\d{1,2}:\d{2}$") || (s.All(char.IsDigit) && s.Length is 3 or 4))
        {
            if (fallbackDate is not { } fd || !TryParseTime(s, out var onlyTime)) return false;
            value = fd.ToDateTime(onlyTime);
            return true;
        }

        // 纯数字：202611162030（12 位）或 20261116（8 位，按当天 00:00）
        if (s.All(char.IsDigit))
        {
            if (s.Length == 12)
                s = $"{s[..4]}-{s[4..6]}-{s[6..8]} {s[8..10]}:{s[10..12]}";
            else if (s.Length == 8)
                s = $"{s[..4]}-{s[4..6]}-{s[6..8]} 00:00";
        }

        // 没写年份：11-16 / 11-16 20:30，按今年算
        if (Regex.IsMatch(s, @"^\d{1,2}-\d{1,2}( \d{1,2}:\d{2})?$"))
            s = $"{DateTime.Today.Year}-{s}";

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
        // 没写年份：11-16，按今年算
        if (Regex.IsMatch(s, @"^\d{1,2}-\d{1,2}$"))
            s = $"{DateTime.Today.Year}-{s}";
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

    /// <summary>把全角字符、斜杠、T 等统一成标准写法，便于后面解析。</summary>
    private static string Normalize(string? text)
    {
        var raw = text ?? string.Empty;
        var chars = raw.Trim().Select(c => c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c).ToArray();
        return new string(chars)
            .Replace('：', ':')
            .Replace('／', '/')
            .Replace('－', '-')
            .Replace('—', '-')
            .Replace('　', ' ')
            .Replace('/', '-')
            .Replace('T', ' ')
            .Replace("  ", " ");
    }
}
