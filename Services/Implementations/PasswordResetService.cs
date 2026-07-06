using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using AttendanceSystem.Data;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 忘记密码找回服务的实现。验证码不落库，存在 <see cref="IMemoryCache"/> 里（本来就是短期一次性数据，
/// 没必要建表）；同时用一个「冷却期」缓存项做发送频率限制，防止被刷。
/// </summary>
public class PasswordResetService(
    AttendanceDbContext db,
    IMemoryCache cache,
    IDingTalkNotifyClient notifyClient,
    ILogger<PasswordResetService> logger) : IPasswordResetService
{
    private const int CodeTtlMinutes  = 10;   // 验证码有效期
    private const int CooldownSeconds = 60;   // 两次发送之间的最短间隔
    private const int MaxAttempts     = 5;    // 验证码最多允许错几次，超了就作废需要重新获取

    /// <summary>缓存里存的验证码条目：验证码本身 + 已错误尝试的次数。</summary>
    private sealed class CodeEntry
    {
        public required string Code { get; init; }
        public int Attempts { get; set; }
    }

    private static string CodeKey(int userId)     => $"pwdreset:code:{userId}";
    private static string CooldownKey(int userId) => $"pwdreset:cooldown:{userId}";

    public async Task<(bool Success, string Message)> RequestCodeAsync(string employeeNo, string phone, CancellationToken ct = default)
    {
        // 工号在系统里是唯一的，手机号只是第二道校验，两者都对上才认为是本人操作，
        // 找不到人时统一回复同一句话，不区分是工号错还是手机号错，避免被用来试探哪些工号存在
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmployeeNo == employeeNo && u.Phone == phone, ct);
        if (user is null)
            return (false, "工号或手机号不正确，请核对后重试");

        if (string.IsNullOrWhiteSpace(user.DingTalkUserId))
            return (false, "该账号尚未绑定钉钉，无法通过此方式找回密码，请联系管理员重置");

        var cooldownKey = CooldownKey(user.Id);
        if (cache.TryGetValue(cooldownKey, out _))
            return (false, $"请求过于频繁，请 {CooldownSeconds} 秒后再试");

        // 6 位数字验证码（000000~999999，不足位数补零）
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        try
        {
            await notifyClient.SendWorkNotificationAsync(user.DingTalkUserId,
                $"您正在找回考勤系统密码，验证码：{code}，{CodeTtlMinutes} 分钟内有效，请勿泄露给他人。", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "找回密码：钉钉工作通知发送失败（用户 {UserId}）", user.Id);
            return (false, "验证码发送失败：" + ex.Message);
        }

        // 发送成功才写入缓存（含冷却期），发送失败不占用冷却期，允许立即重试
        cache.Set(cooldownKey, true, TimeSpan.FromSeconds(CooldownSeconds));
        cache.Set(CodeKey(user.Id), new CodeEntry { Code = code }, TimeSpan.FromMinutes(CodeTtlMinutes));

        logger.LogInformation("找回密码：已向用户 {UserId} 发送验证码", user.Id);
        return (true, $"验证码已通过钉钉工作通知发送，请查收（{CodeTtlMinutes} 分钟内有效）");
    }

    public async Task<(bool Success, string Message)> ResetPasswordAsync(
        string employeeNo, string phone, string code, string newPassword, CancellationToken ct = default)
    {
        if (newPassword.Length < 6)
            return (false, "新密码不能少于 6 位");

        var user = await db.Users.FirstOrDefaultAsync(u => u.EmployeeNo == employeeNo && u.Phone == phone, ct);
        if (user is null)
            return (false, "工号或手机号不正确");

        var codeKey = CodeKey(user.Id);
        if (!cache.TryGetValue(codeKey, out CodeEntry? entry) || entry is null)
            return (false, "验证码不存在或已过期，请重新获取");

        if (entry.Attempts >= MaxAttempts)
        {
            cache.Remove(codeKey);
            return (false, "验证码错误次数过多，请重新获取验证码");
        }

        if (!string.Equals(entry.Code, code.Trim(), StringComparison.Ordinal))
        {
            entry.Attempts++;   // 缓存里存的是引用类型，直接改字段即可，不用重新 Set
            return (false, "验证码不正确");
        }

        cache.Remove(codeKey);   // 一次性验证码：校验通过立即失效，防止被重复使用

        user.PasswordHash = UserService.HashPassword(newPassword);
        user.UpdatedAt    = DateTime.Now;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("找回密码：用户 {UserId} 已通过验证码自助重置密码", user.Id);
        return (true, "密码重置成功，请使用新密码登录");
    }
}
