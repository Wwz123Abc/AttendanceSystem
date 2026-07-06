namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 忘记密码自助找回服务：验证「工号+手机号」匹配后，把一次性验证码通过钉钉工作通知发给本人，
/// 员工凭验证码自助设置新密码（不需要管理员介入）。
/// </summary>
public interface IPasswordResetService
{
    /// <summary>
    /// 校验工号+手机号是否匹配同一账号，匹配则生成验证码并通过钉钉工作通知发送。
    /// 不透露具体是工号还是手机号不对（避免账号被枚举）；未绑定钉钉的账号无法使用此方式。
    /// </summary>
    Task<(bool Success, string Message)> RequestCodeAsync(string employeeNo, string phone, CancellationToken ct = default);

    /// <summary>校验验证码并把密码重置为 <paramref name="newPassword"/>（至少 6 位）。验证码一次性，用过或过期都需要重新获取。</summary>
    Task<(bool Success, string Message)> ResetPasswordAsync(string employeeNo, string phone, string code, string newPassword, CancellationToken ct = default);
}
