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
    [InlineData("2026-11-16", 2026, 11, 16)]
    [InlineData("20261116", 2026, 11, 16)]
    [InlineData("2026/11/16", 2026, 11, 16)]
    public void 日期各种写法都能解析(string text, int y, int mo, int d)
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
