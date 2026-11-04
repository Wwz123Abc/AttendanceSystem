namespace AttendanceSystem.Helpers;

/// <summary>
/// 星期几的中文显示。以前这套映射在报表 DTO、页面视图、后台服务里各写一份（数组或 switch），
/// 改一处容易漏，统一到这里；要改说法只改这一个文件。
/// </summary>
public static class WeekdayText
{
    /// <summary>周日、周一 … 周六。</summary>
    public static string Zh(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday    => "周一",
        DayOfWeek.Tuesday   => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday  => "周四",
        DayOfWeek.Friday    => "周五",
        DayOfWeek.Saturday  => "周六",
        _                   => "周日"
    };

    /// <summary>同上，直接传日期。</summary>
    public static string Zh(DateOnly date) => Zh(date.DayOfWeek);

    /// <summary>短格式：日、一、二 … 六（页面上拼"周X"时用）。</summary>
    public static string ZhShort(DayOfWeek day) => Zh(day)[1..];
}
