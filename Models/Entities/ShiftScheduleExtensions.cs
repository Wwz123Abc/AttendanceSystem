using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>ShiftSchedule 的辅助方法：解析/判断"每周休息日"。</summary>
public static class ShiftScheduleExtensions
{
    /// <summary>把 RestDaysOfWeek 这个逗号分隔字符串解析成星期几的集合。</summary>
    public static HashSet<DayOfWeek> ParseRestDays(this ShiftSchedule shift) =>
        (shift.RestDaysOfWeek ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var n) && n is >= 0 and <= 6 ? (DayOfWeek?)n : null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .ToHashSet();

    /// <summary>某个星期几是不是这个班次配置的"每周休息日"。</summary>
    public static bool IsRestDay(this ShiftSchedule shift, DayOfWeek day) => shift.ParseRestDays().Contains(day);

    /// <summary>
    /// 把 MidCheckWindows 这个逗号分隔字符串解析成"开始-结束"时间段列表，解析不了的段直接跳过。
    /// 重复的时间段（配置时手滑加了两次一模一样的窗口）会被去重——不去重的话，"取最晚一段""排除
    /// 最后一段"这些按下标/时间段区分窗口的逻辑会把同一段窗口误当成两段独立窗口，导致上班时间被
    /// 顺延到窗口结束、下班时间又被同一窗口收窄到窗口开始，两边一起收缩到同一个点，整天工时变成
    /// 0（发现于 2026-09-18 发工资前的数据核查：GA 员工白班/夜班两个班次都配置了同一段窗口两次）。
    /// </summary>
    public static List<(TimeOnly Start, TimeOnly End)> ParseMidCheckWindows(this ShiftSchedule shift) =>
        (shift.MidCheckWindows ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(seg => seg.Split('-'))
            .Where(p => p.Length == 2 && TimeOnly.TryParse(p[0], out _) && TimeOnly.TryParse(p[1], out _))
            .Select(p => (TimeOnly.Parse(p[0]), TimeOnly.Parse(p[1])))
            .Distinct()
            .ToList();

    /// <summary>把"开始-结束"时间段列表格式化回 MidCheckWindows 存库用的字符串。</summary>
    public static string? FormatMidCheckWindows(this List<(TimeOnly Start, TimeOnly End)> windows) =>
        windows.Count == 0 ? null : string.Join(",", windows.Select(w => $"{w.Start:HH\\:mm}-{w.End:HH\\:mm}"));
}
