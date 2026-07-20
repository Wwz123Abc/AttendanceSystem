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

    /// <summary>调钉钉"删除员工"接口（topapi/v2/user/delete），把这个人从企业通讯录里彻底移除。</summary>
    public async Task DeleteEmployeeAsync(string dingTalkUserId, CancellationToken ct = default)
    {
        var token   = await tokenProvider.GetAccessTokenAsync(ct);
        var baseUrl = _opt.OldGatewayBase.TrimEnd('/');
        await PostAsync<DingTalkDeleteUserResponse>(
            $"{baseUrl}/topapi/v2/user/delete?access_token={token}",
            new { userid = dingTalkUserId }, ct);
    }

    /// <summary>
    /// 调钉钉"更新员工"接口（topapi/v2/user/update），把本系统改过的资料同步过去。
    /// 返回值：null=全部同步成功；非空=有一部分（目前只会是手机号）没能同步，这句话是给管理员看的说明，
    /// 但不代表调用失败——其余字段已经同步过去了。
    /// </summary>
    public async Task<string?> UpdateEmployeeAsync(string dingTalkUserId, string? name, string? mobile, string? jobNumber, string? title, List<long>? dingTalkDeptIds = null, CancellationToken ct = default)
    {
        var token   = await tokenProvider.GetAccessTokenAsync(ct);
        var baseUrl = _opt.OldGatewayBase.TrimEnd('/');
        var url     = $"{baseUrl}/topapi/v2/user/update?access_token={token}";
        var req     = new DingTalkUpdateUserRequest
        {
            UserId = dingTalkUserId, Name = name, Mobile = mobile, JobNumber = jobNumber, Title = title,
            DeptIdList = dingTalkDeptIds is { Count: > 0 } ? string.Join(",", dingTalkDeptIds) : null
        };

        try
        {
            await PostAsync<DingTalkUpdateUserResponse>(url, req, ct);
            return null;
        }
        catch (DingTalkApiException ex) when (ex.ErrCode == 40022 && mobile is not null)
        {
            // errcode=40022：钉钉的官方限制——员工本人登录钉钉时用的手机号，和这次要改成的手机号不一致，
            // 钉钉不允许通过接口改这一项（钉钉官方给的说法是"可以删除后重新添加"，但那样太重，本系统不做）。
            // 这不是网络/权限之类的临时故障，重试也没用，所以退一步：把手机号从这次更新里去掉，
            // 只同步姓名/工号/职位这几项，不要因为手机号这一项卡住其余资料的同步。
            req.Mobile = null;
            await PostAsync<DingTalkUpdateUserResponse>(url, req, ct);
            return "手机号与该员工登录钉钉时用的手机号不一致，钉钉不允许通过接口修改（需要员工本人在钉钉里自行更新），其余资料已同步成功";
        }
    }

    /// <summary>调钉钉"新建员工"接口（topapi/v2/user/create），成功后返回钉钉分配的 userid。</summary>
    public async Task<string> CreateEmployeeAsync(string name, string mobile, string? jobNumber, string? title, List<long> dingTalkDeptIds, CancellationToken ct = default)
    {
        var token   = await tokenProvider.GetAccessTokenAsync(ct);
        var baseUrl = _opt.OldGatewayBase.TrimEnd('/');
        var resp = await PostAsync<DingTalkCreateUserResponse>(
            $"{baseUrl}/topapi/v2/user/create?access_token={token}",
            new DingTalkCreateUserRequest
            {
                Name = name, Mobile = mobile, JobNumber = jobNumber, Title = title,
                DeptIdList = string.Join(",", dingTalkDeptIds)
            }, ct);
        return resp.Result?.UserId
            ?? throw new InvalidOperationException("钉钉新建员工接口返回成功，但没有带回 userid");
    }

    /// <summary>调钉钉"新建部门"接口（topapi/v2/department/create），成功后返回钉钉分配的部门编号。</summary>
    public async Task<long> CreateDepartmentAsync(string name, long parentDingTalkDeptId, CancellationToken ct = default)
    {
        var token   = await tokenProvider.GetAccessTokenAsync(ct);
        var baseUrl = _opt.OldGatewayBase.TrimEnd('/');
        var resp = await PostAsync<DingTalkCreateDeptResponse>(
            $"{baseUrl}/topapi/v2/department/create?access_token={token}",
            new DingTalkCreateDeptRequest { Name = name, ParentId = parentDingTalkDeptId }, ct);
        return resp.Result?.DeptId ?? throw new InvalidOperationException("钉钉新建部门接口返回成功，但没有带回部门编号");
    }

    /// <summary>调钉钉"更新部门"接口（topapi/v2/department/update），把本系统改过的部门名/上级部门同步过去。</summary>
    public async Task UpdateDepartmentAsync(long dingTalkDeptId, string? name, long? parentDingTalkDeptId, CancellationToken ct = default)
    {
        var token   = await tokenProvider.GetAccessTokenAsync(ct);
        var baseUrl = _opt.OldGatewayBase.TrimEnd('/');
        await PostAsync<DingTalkUpdateDeptResponse>(
            $"{baseUrl}/topapi/v2/department/update?access_token={token}",
            new DingTalkUpdateDeptRequest { DeptId = dingTalkDeptId, Name = name, ParentId = parentDingTalkDeptId }, ct);
    }

    /// <summary>调钉钉"删除部门"接口（topapi/v2/department/delete）。部门下如果还有钉钉成员，钉钉会拒绝删除。</summary>
    public async Task DeleteDepartmentAsync(long dingTalkDeptId, CancellationToken ct = default)
    {
        var token   = await tokenProvider.GetAccessTokenAsync(ct);
        var baseUrl = _opt.OldGatewayBase.TrimEnd('/');
        await PostAsync<DingTalkDeleteDeptResponse>(
            $"{baseUrl}/topapi/v2/department/delete?access_token={token}",
            new { dept_id = dingTalkDeptId }, ct);
    }

    /// <summary>POST JSON 并反序列化；统一校验钉钉 errcode。</summary>
    private async Task<T> PostAsync<T>(string url, object body, CancellationToken ct) where T : IDingTalkResponse
    {
        var resp = await http.PostAsJsonAsync(url, body, ct);
        resp.EnsureSuccessStatusCode();

        var data = await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException("钉钉通讯录接口响应为空");
        if (data.ErrCode != 0)
            throw new DingTalkApiException(data.ErrCode, data.ErrMsg);
        return data;
    }
}

/// <summary>
/// 钉钉接口返回了非 0 的 errcode 时抛出，把 errcode 单独存一份（ErrCode 属性），
/// 好让调用的地方能针对具体的错误码做不同处理（比如 40022 这种"手机号不一致"的已知限制，需要用不同方式应对），
/// 而不用去解析异常文字里的字符串。
/// </summary>
public class DingTalkApiException(int errCode, string? errMsg)
    : Exception($"钉钉通讯录接口错误：errcode={errCode}, errmsg={errMsg}")
{
    public int ErrCode { get; } = errCode;
}
