namespace AttendanceSystem.Helpers;

/// <summary>
/// 把 <see cref="TimeProvider"/> 换算成本系统一直在用的"本地时间"口径：数据库里存的全是本地时间（不是 UTC），
/// 所以业务代码用 <c>clock.LocalNow()</c> 代替 <c>DateTime.Now</c>、<c>clock.LocalToday()</c> 代替 <c>DateTime.Today</c>，
/// 行为和以前完全一样，只是测试里可以换成假时钟。
/// </summary>
public static class TimeProviderExtensions
{
    /// <summary>当前本地时间（等价于 <c>DateTime.Now</c>）。</summary>
    public static DateTime LocalNow(this TimeProvider clock) => clock.GetLocalNow().DateTime;

    /// <summary>今天本地日期的零点（等价于 <c>DateTime.Today</c>）。</summary>
    public static DateTime LocalToday(this TimeProvider clock) => clock.GetLocalNow().Date;
}
