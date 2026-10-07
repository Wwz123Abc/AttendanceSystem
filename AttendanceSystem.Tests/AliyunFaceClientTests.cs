using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.BackgroundServices;
using AttendanceSystem.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AttendanceSystem.Tests;

/// <summary>
/// 阿里云人脸接口：超时重试预算、熔断与重试的异常分类、失败阶段、空白画面拦截。
/// </summary>
public class AliyunFaceClientTests : SqliteTestBase
{
    // ── 阿里云人脸接口：超时重试不能突破总预算（2026-10-06 复核：故障时每人白等约 16.5 秒）──────────

    private static async Task<(int Result, int Calls, long ElapsedMs, Exception? Error)> RunRetry(Func<int, Task<int>> attemptBody)
    {
        // 生产参数按 1/10 缩小：连接 3000 + 读取 5000 = 单次 8 秒 → 100 + 700 = 0.8 秒；总预算 12000 → 1200；退避 500 → 50
        var opt = new AliyunFaceOptions { ConnectTimeoutMs = 100, ReadTimeoutMs = 700, OverallBudgetMs = 1200, RetryBackoffMs = 50, MaxRetryAttempts = 2 };
        var client = new AliyunFaceClient(Options.Create(opt), NullLogger<AliyunFaceClient>.Instance);
        using var budget = new CancellationTokenSource(opt.OverallBudgetMs);
        var calls = 0;
        var method = typeof(AliyunFaceClient).GetMethod("ExecuteWithRetryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(typeof(int));
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await (Task<int>)method.Invoke(client, [new Func<Task<int>>(() => attemptBody(++calls)), Array.Empty<MemoryStream>(), CancellationToken.None, budget.Token])!;
            return (result, calls, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) { return (0, calls, sw.ElapsedMilliseconds, ex); }
    }

    [Fact]
    public async Task 人脸接口_单次尝试就卡满超时_剩余预算不够再来一次_不再重试_按一次的时长失败()
    {
        var r = await RunRetry(async _ => { await Task.Delay(800); throw new System.Net.WebException("operation is timeout"); });

        Assert.IsType<System.Net.WebException>(r.Error);   // 原样抛出（上层会计入熔断），不是被总预算取消
        Assert.Equal(1, r.Calls);                           // 修复前是 2 次（换算到生产就是 16.5 秒）
        Assert.InRange(r.ElapsedMs, 700, 1500);             // 约 0.8 秒；修复前约 1.7 秒
    }

    [Fact]
    public async Task 人脸接口_几乎立刻返回的瞬时错误_剩余预算够_照常重试并成功()
    {
        var r = await RunRetry(n => n == 1 ? throw new System.Net.WebException("operation is timeout") : Task.FromResult(7));

        Assert.Null(r.Error);
        Assert.Equal(7, r.Result);
        Assert.Equal(2, r.Calls);
    }

    // ── 远程打卡：空白/非 JPEG 的画面不能传给阿里云（2026-10-04 阿里云确认收到的图片是空白的）──────

    [Fact]
    public void 远程打卡_空数据和非JPEG画面被判为不可用_正常JPEG通过()
    {
        // 摄像头没出画面时，前端 toDataURL 返回 "data:,"，服务端解码出来是 0 字节
        Assert.False(AttendanceSystem.Pages.Attendance.RemotePunchModel.IsUsableJpeg(Convert.FromBase64String("")));
        Assert.False(AttendanceSystem.Pages.Attendance.RemotePunchModel.IsUsableJpeg(new byte[] { 0xFF, 0xD8, 0xFF }));   // 太小
        Assert.False(AttendanceSystem.Pages.Attendance.RemotePunchModel.IsUsableJpeg(new byte[4096]));                    // 全 0，不是 JPEG
        var ok = new byte[4096]; ok[0] = 0xFF; ok[1] = 0xD8;
        Assert.True(AttendanceSystem.Pages.Attendance.RemotePunchModel.IsUsableJpeg(ok));
    }

    [Fact]
    public void 人脸接口_失败阶段分类_授权_OSS上传_识别接口三步分得清()
    {
        var typ = typeof(AliyunFaceClient);
        string Classify(string? stack) => (string)typ.GetMethod("ClassifyStageFromStack",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object?[] { stack })!;

        // 2026-10-04 日志里三种真实堆栈
        var oss  = "at Client._postOSSObjectAsync(...) at Client.DetectLivingFaceAdvanceAsync(...)";
        var auth = "at Client.DoRequestAsync(...) at Client.CallApiAsync(...) at Client.DetectLivingFaceAdvanceAsync(...)";
        var api  = "at Client.DoRequestAsync(...) at Client.CallApiAsync(...) at Client.DetectLivingFaceWithOptionsAsync(...) at Client.DetectLivingFaceAdvanceAsync(...)";

        Assert.Contains("OSS", Classify(oss));
        Assert.Contains("授权", Classify(auth));
        Assert.Contains("识别接口", Classify(api));
        Assert.Equal("未知", Classify(null));
    }

    // ── 阿里云人脸接口：超时类异常要计入熔断、也要重试（2026-10-04 早高峰实测没计入）──────────

    private static T CallFaceClientStatic<T>(string name, params object[] args)
    {
        var m = typeof(AliyunFaceClient).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (T)m.Invoke(null, args)!;
    }

    [Fact]
    public void 人脸接口_Tea_SDK超时抛的WebException_计入熔断也算可重试()
    {
        // 生产日志里 10/4 早上 1100 多次失败的真实异常就是这个：System.Net.WebException: operation is timeout
        var timeout = new System.Net.WebException("operation is timeout");
        Assert.True(CallFaceClientStatic<bool>("ShouldCountForCircuitBreaker", timeout));
        Assert.True(CallFaceClientStatic<bool>("IsRetryable", timeout, CancellationToken.None));
    }

    [Fact]
    public void 人脸接口_被限流_照常重试但不计入熔断()
    {
        // 生产日志里的真实异常：code: 400, 调用被限流(...当前QPS:3,QPS阈值:2)——只说明请求太密，不说明服务坏了
        var throttled = new Tea.TeaException(new Dictionary<string, object>
        {
            ["code"] = "Throttling.User", ["message"] = "调用被限流", ["data"] = new Dictionary<string, object> { ["statusCode"] = 400 },
        });
        Assert.True(CallFaceClientStatic<bool>("IsRetryable", throttled, CancellationToken.None));
        Assert.False(CallFaceClientStatic<bool>("ShouldCountForCircuitBreaker", throttled));
    }

    [Fact]
    public void 人脸接口_参数类错误不计入熔断_调用方自己取消的不重试()
    {
        var bad = new InvalidOperationException("参数错误");
        Assert.False(CallFaceClientStatic<bool>("ShouldCountForCircuitBreaker", bad));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(CallFaceClientStatic<bool>("IsRetryable", new System.Net.WebException("operation is timeout"), cts.Token));   // 调用方已经取消，不再白花调用量
    }
}
