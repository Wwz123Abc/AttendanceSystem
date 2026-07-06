using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 钉钉请假接口客户端，封装 topapi/attendance/getleavestatus（请假状态）接口。
/// 钉钉限制：单批用户 ≤100、单页 ≤20，这里自动分批 + 翻页处理。
/// </summary>
public class DingTalkLeaveClient(
    HttpClient http,
    IDingTalkTokenProvider tokenProvider,
    IOptions<DingTalkOptions> options,
    ILogger<DingTalkLeaveClient> logger) : IDingTalkLeaveClient
{
    private readonly DingTalkOptions _opt = options.Value;

    public async Task<List<DingTalkLeaveStatus>> ListLeaveStatusAsync(
        IReadOnlyCollection<string> dingTalkUserIds,
        DateTime from, DateTime to,
        CancellationToken ct = default)
    {
        var result = new List<DingTalkLeaveStatus>();
        if (dingTalkUserIds.Count == 0) return result;

        int userBatch = Math.Max(1, _opt.LeaveMaxUsersPerRequest);   // 单批用户 ≤100
        int pageSize  = Math.Max(1, _opt.LeavePageSize);             // 单页 ≤20

        var token = await tokenProvider.GetAccessTokenAsync(ct);
        var url   = $"{_opt.OldGatewayBase.TrimEnd('/')}/topapi/attendance/getleavestatus?access_token={token}";

        long startMs = new DateTimeOffset(from).ToUnixTimeMilliseconds();
        long endMs   = new DateTimeOffset(to).ToUnixTimeMilliseconds();

        foreach (var batch in dingTalkUserIds.Chunk(userBatch))          // ① 用户分批
        {
            for (long offset = 0; ; offset += pageSize)                  // ② 翻页
            {
                var req = new DingTalkLeaveStatusRequest
                {
                    UserIdList = string.Join(',', batch),
                    StartTime  = startMs,
                    EndTime    = endMs,
                    Offset     = offset,
                    Size       = pageSize
                };

                var resp = await http.PostAsJsonAsync(url, req, ct);
                resp.EnsureSuccessStatusCode();

                var body = await resp.Content.ReadFromJsonAsync<DingTalkLeaveStatusResponse>(cancellationToken: ct)
                    ?? throw new InvalidOperationException("钉钉 getleavestatus 响应为空");
                if (body.ErrCode != 0)
                    throw new InvalidOperationException(
                        $"钉钉 getleavestatus 返回错误：errcode={body.ErrCode}, errmsg={body.ErrMsg}" +
                        "（请确认已开通「考勤/假期数据」相关权限）");

                if (body.Result?.LeaveStatus is { Count: > 0 } items)
                    result.AddRange(items);

                if (body.Result?.HasMore != true) break;   // 本批次没有更多数据，结束翻页
            }
        }

        logger.LogInformation("钉钉请假记录拉取完成，共 {Count} 条（{From:yyyy-MM-dd} ~ {To:yyyy-MM-dd}）",
            result.Count, from, to);
        return result;
    }
}
