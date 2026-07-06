using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 钉钉考勤接口客户端，封装 attendance/list 打卡结果接口。
/// 钉钉对该接口有三大限制，这里全部自动处理：时间跨度 ≤7 天、单批用户 ≤50 人、单页 ≤50 条。
/// </summary>
public class DingTalkAttendanceClient(
    HttpClient http,
    IDingTalkTokenProvider tokenProvider,
    IOptions<DingTalkOptions> options,
    ILogger<DingTalkAttendanceClient> logger) : IDingTalkAttendanceClient
{
    private readonly DingTalkOptions _opt = options.Value;

    public async Task<List<DingTalkRecordResult>> ListAttendanceAsync(
        IReadOnlyCollection<string> dingTalkUserIds,
        DateTime from, DateTime to,
        CancellationToken ct = default)
    {
        var result = new List<DingTalkRecordResult>();
        if (dingTalkUserIds.Count == 0) return result;

        // 三个限制值兜底为 ≥1，避免切片/分批时出现非法步长
        int maxDays   = Math.Max(1, _opt.MaxDaysPerRequest);
        int userBatch = Math.Max(1, _opt.MaxUsersPerRequest);
        int pageSize  = Math.Max(1, _opt.PageSize);

        var token = await tokenProvider.GetAccessTokenAsync(ct);
        var url   = $"{_opt.OldGatewayBase.TrimEnd('/')}/attendance/list?access_token={token}";

        // 三层循环正好对应钉钉的三个限制：① 时间切片 ② 用户分批 ③ 单页翻页
        foreach (var (segFrom, segTo) in SplitByDays(from, to, maxDays))          // ① ≤7 天
        foreach (var batch in dingTalkUserIds.Chunk(userBatch))                   // ② ≤50 人
        {
            for (int offset = 0; ; offset += pageSize)                            // ③ ≤50 条，翻页
            {
                var req = new DingTalkAttendanceListRequest
                {
                    WorkDateFrom = segFrom.ToString("yyyy-MM-dd HH:mm:ss"),
                    WorkDateTo   = segTo.ToString("yyyy-MM-dd HH:mm:ss"),
                    UserIdList   = [.. batch],
                    Offset       = offset,
                    Limit        = pageSize,
                    IsI18n       = false
                };

                var resp = await http.PostAsJsonAsync(url, req, ct);
                resp.EnsureSuccessStatusCode();

                var body = await resp.Content.ReadFromJsonAsync<DingTalkAttendanceListResponse>(cancellationToken: ct)
                    ?? throw new InvalidOperationException("钉钉 attendance/list 响应为空");
                if (body.ErrCode != 0)
                    throw new InvalidOperationException(
                        $"钉钉 attendance/list 返回错误：errcode={body.ErrCode}, errmsg={body.ErrMsg}");

                if (body.RecordResult is { Count: > 0 })
                    result.AddRange(body.RecordResult);

                if (!body.HasMore) break;   // 本批次没有更多数据，结束翻页
            }
        }

        logger.LogInformation("钉钉打卡结果拉取完成，共 {Count} 条（{From:yyyy-MM-dd} ~ {To:yyyy-MM-dd}）",
            result.Count, from, to);
        return result;
    }

    /// <summary>把 [from, to] 按 maxDays 天为粒度切成若干不重叠区间（含首尾）。</summary>
    private static IEnumerable<(DateTime From, DateTime To)> SplitByDays(DateTime from, DateTime to, int maxDays)
    {
        for (var cursor = from; cursor <= to; )
        {
            var segEnd = cursor.AddDays(maxDays).AddSeconds(-1);   // 本段终点，跨度刚好不超过 maxDays 天
            if (segEnd > to) segEnd = to;
            yield return (cursor, segEnd);
            cursor = segEnd.AddSeconds(1);                        // 下一段从上段终点的下一秒开始
        }
    }
}
