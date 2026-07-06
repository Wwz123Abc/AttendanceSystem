namespace AttendanceSystem.Services.Interfaces;

/// <summary>钉钉 accessToken 提供者（带缓存，避免每次调用都重新获取）。</summary>
public interface IDingTalkTokenProvider
{
    /// <summary>获取有效的 accessToken（命中缓存则直接返回，否则远程获取并缓存）。</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}
