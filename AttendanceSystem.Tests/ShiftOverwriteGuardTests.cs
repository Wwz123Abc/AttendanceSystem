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

    [Fact]
    public void 已排班列表_被排在别的考勤组班次上的人要能认出来()
    {
        // 汤晓永属新能源-客服，排的是新能源-生产的白班：这一行的班次所属组 = 生产，他在 Foreign 里，页面上标红
        var row = new ShiftManageModel.AssignmentRow(new DateOnly(2026, 10, 12), "XNY白班", "#3b82f6",
            ["汤晓永", "王波1"], "新能源-生产", [("汤晓永", "新能源-客服"), ("王波1", "新能源-客服")]);
        Assert.True(row.IsForeign("汤晓永"));
        Assert.Equal(2, row.Foreign.Count);
        Assert.Equal("周一", row.WeekText);

        var normal = new ShiftManageModel.AssignmentRow(new DateOnly(2026, 10, 12), "白班1", "#3b82f6", ["刘洋洋"], "新能源-客服", []);
        Assert.False(normal.IsForeign("刘洋洋"));
    }
}
