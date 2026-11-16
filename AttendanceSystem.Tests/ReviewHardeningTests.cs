using AttendanceSystem.Helpers;
using AttendanceSystem.Pages.Attendance;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-11-16 复审 P2 的回归测试：
/// ① 人脸照片解码前必须先按像素预算拦截（小文件、高像素的“解压炸弹”）；
/// ② 远程打卡的抓拍图必须是完整 JPEG（有 EOI 结束标记），不能被截断数据糊弄过去；
/// ③ 远程打卡要有独立的请求频率限制（失败限流挡不住不产生失败记录的请求）。
/// </summary>
public class ReviewHardeningTests
{
    [Theory]
    [InlineData(6000, 4000, true)]     // 2400 万像素，普通手机照片
    [InlineData(8000, 6000, true)]     // 4800 万像素，高像素手机，仍在预算内
    [InlineData(10000, 6000, false)]   // 6000 万像素，超过 5000 万上限
    [InlineData(20000, 20000, false)]  // 4 亿像素，典型解压炸弹
    [InlineData(0, 4000, false)]       // 宽高非法
    public void 人脸照片像素预算拦截(int width, int height, bool expected)
    {
        Assert.Equal(expected, ImageValidationHelper.IsWithinDecodePixelBudget(width, height));
    }

    private static byte[] Jpeg(int length, bool eoi = true)
    {
        var bytes = new byte[length];
        bytes[0] = 0xFF; bytes[1] = 0xD8;                       // SOI
        if (eoi) { bytes[^2] = 0xFF; bytes[^1] = 0xD9; }         // EOI
        return bytes;
    }

    [Fact]
    public void 完整JPEG可以通过()
    {
        Assert.True(RemotePunchModel.IsUsableJpeg(Jpeg(2048)));
    }

    [Fact]
    public void 截断的JPEG_没有EOI_不能通过()
    {
        Assert.False(RemotePunchModel.IsUsableJpeg(Jpeg(2048, eoi: false)));
    }

    [Fact]
    public void 文件太小不能通过()
    {
        Assert.False(RemotePunchModel.IsUsableJpeg(Jpeg(512)));
    }

    [Fact]
    public void 请求限流_窗口内超过上限就拒绝_窗口过后恢复()
    {
        RequestRateLimiter.Reset();
        var start = new DateTime(2026, 11, 16, 9, 0, 0);
        var window = TimeSpan.FromMinutes(10);
        const int max = 3;

        for (var i = 0; i < max; i++)
            Assert.True(RequestRateLimiter.TryAcquire("test:1", max, window, start.AddSeconds(i), out _));

        Assert.False(RequestRateLimiter.TryAcquire("test:1", max, window, start.AddSeconds(max), out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        // 窗口过后（第一条记录已过期）可以继续
        Assert.True(RequestRateLimiter.TryAcquire("test:1", max, window, start.Add(window), out _));
    }

    [Fact]
    public void 请求限流_不同用户互不影响()
    {
        RequestRateLimiter.Reset();
        var now = new DateTime(2026, 11, 16, 9, 0, 0);
        Assert.True(RequestRateLimiter.TryAcquire("test:A", 1, TimeSpan.FromMinutes(10), now, out _));
        Assert.False(RequestRateLimiter.TryAcquire("test:A", 1, TimeSpan.FromMinutes(10), now, out _));
        Assert.True(RequestRateLimiter.TryAcquire("test:B", 1, TimeSpan.FromMinutes(10), now, out _));
    }
}
