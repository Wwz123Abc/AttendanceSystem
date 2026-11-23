using AttendanceSystem.Helpers;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-11-20：移动端日期/时间改成文本输入后的容错解析。
/// </summary>
public class DateTimeTextParserTests
{
    [Theory]
    [InlineData("2026-11-16 20:30", 2026, 11, 16, 20, 30)]
    [InlineData("2026-11-16T20:30", 2026, 11, 16, 20, 30)]
    [InlineData("202611162030", 2026, 11, 16, 20, 30)]
    [InlineData("2026/11/16 20:30", 2026, 11, 16, 20, 30)]
    [InlineData("20261116", 2026, 11, 16, 0, 0)]
    public void 日期时间各种写法都能解析(string text, int y, int mo, int d, int h, int mi)
    {
        Assert.True(DateTimeTextParser.TryParseDateTime(text, out var value), text);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), value);
    }

    [Theory]
    [InlineData("11-21 22:47", 11, 21, 22, 47)]
    [InlineData("11-21 8:30", 11, 21, 8, 30)]
    [InlineData("１１-２１ ２２:４７", 11, 21, 22, 47)]   // 全角数字
    public void 没写年份的日期时间按今年算(string text, int mo, int d, int h, int mi)
    {
        Assert.True(DateTimeTextParser.TryParseDateTime(text, out var value), text);
        Assert.Equal(new DateTime(DateTime.Today.Year, mo, d, h, mi, 0), value);
    }

    [Fact]
    public void 结束时间只填钟点时用开始日期补()
    {
        var day = new DateOnly(2026, 11, 21);
        Assert.True(DateTimeTextParser.TryParseDateTime("20:30", out var value, day));
        Assert.Equal(day.ToDateTime(new TimeOnly(20, 30)), value);
        Assert.False(DateTimeTextParser.TryParseDateTime("20:30", out _));   // 开始时间必须有日期，不能只填钟点
    }

    [Theory]
    [InlineData("11-16", 2026, 11, 16)]
    [InlineData("１１-１６", 2026, 11, 16)]
    public void 没写年份的日期按今年算(string text, int y, int mo, int d)
    {
        Assert.True(DateTimeTextParser.TryParseDate(text, out var value), text);
        Assert.Equal(new DateOnly(y, mo, d), value);
    }

    [Theory]
    [InlineData("2026-11-16", 2026, 11, 16)]
    [InlineData("20261116", 2026, 11, 16)]
    [InlineData("2026/11/16", 2026, 11, 16)]
    public void 完整日期各种写法都能解析(string text, int y, int mo, int d)
    {
        Assert.True(DateTimeTextParser.TryParseDate(text, out var value), text);
        Assert.Equal(new DateOnly(y, mo, d), value);
    }

    [Theory]
    [InlineData("20:30", 20, 30)]
    [InlineData("2030", 20, 30)]
    [InlineData("930", 9, 30)]
    [InlineData("20：30", 20, 30)]
    public void 时间各种写法都能解析(string text, int h, int mi)
    {
        Assert.True(DateTimeTextParser.TryParseTime(text, out var value), text);
        Assert.Equal(new TimeOnly(h, mi), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("2026-13-40")]
    public void 明显非法的时间返回false(string text)
    {
        Assert.False(DateTimeTextParser.TryParseDateTime(text, out _));
        Assert.False(DateTimeTextParser.TryParseDate(text, out _));
        Assert.False(DateTimeTextParser.TryParseTime(text, out _));
    }
}
