namespace AttendanceSystem.Services.Interfaces;

/// <summary>钉钉"工作通知"客户端：给指定员工的钉钉账号推送一条应用消息（目前用于找回密码验证码）。</summary>
public interface IDingTalkNotifyClient
{
    /// <summary>给指定钉钉 userid 发送一条文本工作通知。失败会抛异常（含钉钉返回的 errcode/errmsg）。</summary>
    Task SendWorkNotificationAsync(string dingTalkUserId, string content, CancellationToken ct = default);
}
