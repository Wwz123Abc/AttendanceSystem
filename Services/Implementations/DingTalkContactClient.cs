using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 钉钉通讯录客户端。流程：取根部门名作公司名 → 从根部门递归取所有部门 →
/// 逐个部门翻页拉成员（topapi/v2/user/list），按 userid 去重。
/// </summary>
public class DingTalkContactClient(
    HttpClient http,
    IDingTalkTokenProvider tokenProvider,
    IOptions<DingTalkOptions> options,
    ILogger<DingTalkContactClient> logger) : IDingTalkContactClient
{
    private readonly DingTalkOptions _opt = options.Value;

    public async Task<DingTalkContactSnapshot> ListAllAsync(CancellationToken ct = default)
    {
        var token   = await tokenProvider.GetAccessTokenAsync(ct);
        var baseUrl = _opt.OldGatewayBase.TrimEnd('/');
        var snapshot = new DingTalkContactSnapshot();

        // 0) 根部门(1)的名称作为「所属公司」
        var root = await PostAsync<DingTalkDeptGetResponse>(
            $"{baseUrl}/topapi/v2/department/get?access_token={token}",
            new { dept_id = 1 }, ct);
        snapshot.CompanyName = root.Result?.Name;
        snapshot.Departments.Add(new DingTalkDept { DeptId = 1, Name = root.Result?.Name, ParentId = 0 });

        // 1) 从根部门广度遍历，收集所有子部门
        var deptIds = new HashSet<long> { 1 };
        var queue   = new Queue<long>();
        queue.Enqueue(1);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var subs = await PostAsync<DingTalkDeptListResponse>(
                $"{baseUrl}/topapi/v2/department/listsub?access_token={token}",
                new { dept_id = parent }, ct);

            foreach (var d in subs.Result ?? [])
                if (deptIds.Add(d.DeptId))
                {
                    snapshot.Departments.Add(d);
                    queue.Enqueue(d.DeptId);
                }
        }

        // 2) 每个部门翻页拉成员（size≤100），按 userid 去重（一人可能在多个部门）
        var users = new Dictionary<string, DingTalkDeptUser>();
        foreach (var deptId in deptIds)
        {
            for (int cursor = 0; ; )
            {
                var resp = await PostAsync<DingTalkUserListResponse>(
                    $"{baseUrl}/topapi/v2/user/list?access_token={token}",
                    new { dept_id = deptId, cursor, size = 100, contain_access_limit = false, language = "zh_CN" }, ct);

                foreach (var u in resp.Result?.List ?? [])
                    if (!string.IsNullOrEmpty(u.UserId))
                        users[u.UserId] = u;

                if (resp.Result is null || !resp.Result.HasMore) break;
                cursor = resp.Result.NextCursor;
            }
        }

        snapshot.Users = users.Values.ToList();
        logger.LogInformation("钉钉通讯录拉取完成：公司「{Company}」，部门 {Depts} 个，员工 {Users} 人",
            snapshot.CompanyName, snapshot.Departments.Count, snapshot.Users.Count);
        return snapshot;
    }

    /// <summary>POST JSON 并反序列化；统一校验钉钉 errcode。</summary>
    private async Task<T> PostAsync<T>(string url, object body, CancellationToken ct) where T : IDingTalkResponse
    {
        var resp = await http.PostAsJsonAsync(url, body, ct);
        resp.EnsureSuccessStatusCode();

        var data = await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException("钉钉通讯录接口响应为空");
        if (data.ErrCode != 0)
            throw new InvalidOperationException(
                $"钉钉通讯录接口错误：errcode={data.ErrCode}, errmsg={data.ErrMsg}");
        return data;
    }
}
