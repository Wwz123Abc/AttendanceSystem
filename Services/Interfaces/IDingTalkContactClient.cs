using AttendanceSystem.Models.DTOs;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>钉钉通讯录客户端：拉取公司名、全部部门、全部员工（含 userid / 姓名 / 手机号 / 部门）。</summary>
public interface IDingTalkContactClient
{
    /// <summary>
    /// 一次遍历返回通讯录快照：公司名 + 所有部门 + 所有在职员工（按 userid 去重）。
    /// 需要应用已发布且可用范围为「全部员工」，否则只能读到范围内的人。
    /// </summary>
    Task<DingTalkContactSnapshot> ListAllAsync(CancellationToken ct = default);
}
