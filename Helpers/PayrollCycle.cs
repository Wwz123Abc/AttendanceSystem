namespace AttendanceSystem.Helpers;

/// <summary>
/// 薪资周期：每月 26 日到下月 25 日，按"结束那个月"命名——10 月周期 = 9 月 26 日 ~ 10 月 25 日
/// （2026-10 用户确认；跟"发薪考勤汇总表"选的起止日期是同一个口径）。
/// "我的记录""我的日历"都按它显示，员工页面上的总工时才能跟发薪汇总表对得上。
/// </summary>
public static class PayrollCycle
{
    /// <summary>每个周期从上月的这一天开始。</summary>
    public const int StartDay = 26;

    /// <summary>每个周期到本月的这一天结束。</summary>
    public const int EndDay = 25;

    /// <summary>结束于 <paramref name="endYear"/> 年 <paramref name="endMonth"/> 月的那个周期的起止日期。</summary>
    public static (DateOnly Start, DateOnly End) Range(int endYear, int endMonth)
    {
        var end = new DateOnly(endYear, endMonth, EndDay);
        var start = new DateOnly(endYear, endMonth, StartDay).AddMonths(-1);
        return (start, end);
    }

    /// <summary>包含 <paramref name="day"/> 这一天的周期，用"结束月份"表示：26 日起算下个月的周期。</summary>
    public static (int Year, int Month) EndMonthOf(DateOnly day)
    {
        var m = day.Day >= StartDay ? new DateOnly(day.Year, day.Month, 1).AddMonths(1) : new DateOnly(day.Year, day.Month, 1);
        return (m.Year, m.Month);
    }

    /// <summary>
    /// 把网址里的 year/month（周期的结束月份）规整成合法值：缺省、非法、超出"最近 24 个月到下个月"的，
    /// 一律退回当前所在周期——任何登录员工改网址遍历年月都不会触发大量查询。
    /// </summary>
    public static (int Year, int Month) ResolveRequested(int? year, int? month, DateOnly today)
    {
        var (curY, curM) = EndMonthOf(today);
        // 传了但不合法（如 month=13、year=1999）：整个退回当前周期，不拼凑出一个年月
        if (year is not null and (< 2000 or > 2100)) return (curY, curM);
        if (month is not null and (< 1 or > 12)) return (curY, curM);
        var y = year ?? curY;
        var m = month ?? curM;
        var requested = new DateOnly(y, m, 1);
        var current   = new DateOnly(curY, curM, 1);
        if (requested < current.AddMonths(-24) || requested > current.AddMonths(1)) return (curY, curM);
        return (y, m);
    }

    /// <summary>周期的文字说明，如"9月26日 ~ 10月25日"。</summary>
    public static string Describe(DateOnly start, DateOnly end)
        => $"{start.Month}月{start.Day}日 ~ {end.Month}月{end.Day}日";
}
