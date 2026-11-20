using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 补卡页面「打卡时间」的容错解析（2026-11-20）：部分手机浏览器的时间控件显示有值但实际没提交，
/// 改成文本框后允许直接输 2030 / 930 / 20：30，服务器统一转成标准时间。
/// </summary>
public class PunchTimeTextTests
{
    [Theory]
    [InlineData("20:30", 20, 30)]
    [InlineData("2030", 20, 30)]
    [InlineData("930", 9, 30)]
    [InlineData("20：30", 20, 30)]
    [InlineData(" 8:05 ", 8, 5)]
    public void 各种常见输入都能解析(string text, int hour, int minute)
    {
        Assert.True(PunchReplenishmentRules.TryParseClockText(text, out var time, out var error), error);
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("2560")]
    [InlineData("24:00")]
    public void 空值和非法时间会给出提示(string text)
    {
        Assert.False(PunchReplenishmentRules.TryParseClockText(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
