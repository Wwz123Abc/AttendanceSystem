using AttendanceSystem.Pages.Admin;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>批量排班不能悄悄覆盖"今天之前"已排过别的班次的日期（2026-10-21：夜班排给两个人时覆盖了他们 9/26–10/20 的白班，历史工时对不上）。</summary>
public class ShiftOverwriteGuardTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    [Theory]
    [InlineData(-1, 22, 23, false, true)]    // 昨天已排白班，要改成夜班 → 跳过
    [InlineData(-30, 22, 23, false, true)]   // 一个月前同理
    [InlineData(-1, 22, 23, true, false)]    // 勾了"允许覆盖过去的排班" → 放行
    [InlineData(-1, 23, 23, false, false)]   // 过去的日期但班次本来就一样 → 不用跳过
    [InlineData(0, 22, 23, false, false)]    // 今天可以改
    [InlineData(5, 22, 23, false, false)]    // 未来可以改
    public void 过去已排班的日期_默认不覆盖(int dayOffset, int existingShift, int newShift, bool allowPast, bool expectedSkip) =>
        Assert.Equal(expectedSkip, ShiftManageModel.ShouldSkipPastOverwrite(Today.AddDays(dayOffset), Today, existingShift, newShift, allowPast));
}
