using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 钉钉工作通知客户端，封装 topapi/message/corpconversation/asyncsend_v2 接口。
/// 目前唯一用途：找回密码时把验证码推给员工的钉钉账号。
/// </summary>
public class DingTalkNotifyClient(
    HttpClient http,
    IDingTalkTokenProvider tokenProvider,
    IOptions<DingTalkOptions> options,
    ILogger<DingTalkNotifyClient> logger) : IDingTalkNotifyClient
{
    private readonly DingTalkOptions _opt = options.Value;

    public async Task SendWorkNotificationAsync(string dingTalkUserId, string content, CancellationToken ct = default)
    {
        if (!long.TryParse(_opt.AgentId, out var agentId) || agentId <= 0)
            throw new InvalidOperationException("未正确配置钉钉 AgentId（appsettings.json 的 DingTalk:AgentId 应为纯数字，在钉钉后台该应用的「应用信息」页可查看）");

        var token = await tokenProvider.GetAccessTokenAsync(ct);
        var url   = $"{_opt.OldGatewayBase.TrimEnd('/')}/topapi/message/corpconversation/asyncsend_v2?access_token={token}";

        var req = new DingTalkSendMsgRequest
        {
            AgentId    = agentId,
            UserIdList = dingTalkUserId,
            Msg        = new DingTalkMsgBody { MsgType = "text", Text = new DingTalkMsgText { Content = content } }
        };

        var resp = await http.PostAsJsonAsync(url, req, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<DingTalkSendMsgResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("钉钉工作通知接口响应为空");
        if (body.ErrCode != 0)
            throw new InvalidOperationException(
                $"钉钉工作通知发送失败：errcode={body.ErrCode}, errmsg={body.ErrMsg}" +
                "（请确认 AgentId 正确，且该应用已开通「工作通知」接口权限）");

        logger.LogInformation("钉钉工作通知已发送给 userid={UserId}", dingTalkUserId);
    }
}
