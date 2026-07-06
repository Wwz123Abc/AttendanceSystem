using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 钉钉 accessToken 提供者。
/// 调用新版网关 /v1.0/oauth2/accessToken 获取令牌，并用 IMemoryCache 缓存，
/// 提前 5 分钟过期以避免临界失效。
/// </summary>
public class DingTalkTokenProvider(
    HttpClient http,
    IMemoryCache cache,
    IOptions<DingTalkOptions> options,
    ILogger<DingTalkTokenProvider> logger) : IDingTalkTokenProvider
{
    private const string CacheKey = "DingTalk:AccessToken";
    private readonly DingTalkOptions _opt = options.Value;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
            return cached!;

        if (string.IsNullOrWhiteSpace(_opt.AppKey) || string.IsNullOrWhiteSpace(_opt.AppSecret))
            throw new InvalidOperationException("未配置钉钉 AppKey / AppSecret（appsettings.json 的 DingTalk 节）");

        var url  = $"{_opt.NewGatewayBase.TrimEnd('/')}/v1.0/oauth2/accessToken";
        var resp = await http.PostAsJsonAsync(url,
            new { appKey = _opt.AppKey, appSecret = _opt.AppSecret }, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<DingTalkTokenResponse>(cancellationToken: ct);
        if (body is null || string.IsNullOrEmpty(body.AccessToken))
            throw new InvalidOperationException("获取钉钉 accessToken 失败：响应为空或无 accessToken");

        // 提前 5 分钟过期；最少缓存 60 秒
        var ttl = TimeSpan.FromSeconds(Math.Max(60, body.ExpireIn - 300));
        cache.Set(CacheKey, body.AccessToken, ttl);
        logger.LogInformation("已获取钉钉 accessToken，有效期 {Sec}s，本地缓存 {Ttl}", body.ExpireIn, ttl);
        return body.AccessToken!;
    }
}
