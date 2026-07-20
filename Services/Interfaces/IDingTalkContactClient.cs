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

    /// <summary>
    /// 从钉钉通讯录里彻底删除一名员工。钉钉开放平台对普通企业应用只提供"删除"，没有"临时禁用"，
    /// 所以这个操作删掉之后，如果以后要恢复，需要对方重新扫码/接受邀请加回通讯录，无法用接口撤销。
    /// </summary>
    Task DeleteEmployeeAsync(string dingTalkUserId, CancellationToken ct = default);

    /// <summary>
    /// 把本系统里已经改过的姓名/手机号/工号/职位/所在部门，同步更新到钉钉通讯录里对应的员工身上（本系统 → 钉钉）。
    /// 只能用于已经绑定过 DingTalkUserId 的员工；哪个参数传 null 就表示钉钉那边这一项不用改
    /// （dingTalkDeptIds 传 null 表示部门不变，通常是因为新部门还没同步出钉钉编号，换算不出来）。
    /// 返回值：null=全部同步成功；非空=钉钉那边的已知限制导致手机号没能同步（其余字段仍已同步），
    /// 这句话是给管理员看的说明，不代表整体失败。
    /// </summary>
    Task<string?> UpdateEmployeeAsync(string dingTalkUserId, string? name, string? mobile, string? jobNumber, string? title, List<long>? dingTalkDeptIds = null, CancellationToken ct = default);

    /// <summary>
    /// 在钉钉通讯录里新建一名员工（本系统 → 钉钉）。mobile 在钉钉企业内必须唯一，dingTalkDeptIds
    /// 是这个人要挂到的钉钉部门编号（调用前要先换算好，换不出来就不能调这个接口）。
    /// 成功后返回钉钉分配的 userid，本地要把它存回 User.DingTalkUserId，以后编辑/删除才能继续联动。
    /// </summary>
    Task<string> CreateEmployeeAsync(string name, string mobile, string? jobNumber, string? title, List<long> dingTalkDeptIds, CancellationToken ct = default);

    /// <summary>
    /// 在钉钉通讯录里新建一个部门（本系统 → 钉钉）。parentDingTalkDeptId 是这个部门在钉钉那边的
    /// 上级部门编号，顶级部门传 1（钉钉根部门）。成功后返回钉钉分配的部门编号，本地要存回
    /// Department.DingTalkDeptId，以后改名/删除才能继续联动。
    /// </summary>
    Task<long> CreateDepartmentAsync(string name, long parentDingTalkDeptId, CancellationToken ct = default);

    /// <summary>
    /// 把本系统里已经改过的部门名称/上级部门，同步更新到钉钉里对应的部门（本系统 → 钉钉）。
    /// 只能用于已经有 DingTalkDeptId 的部门；哪个参数传 null 就表示这一项不用改
    /// （parentDingTalkDeptId 传 null 通常是因为新上级部门还没同步出钉钉编号）。
    /// </summary>
    Task UpdateDepartmentAsync(long dingTalkDeptId, string? name, long? parentDingTalkDeptId, CancellationToken ct = default);

    /// <summary>在钉钉里彻底删除一个部门（本系统 → 钉钉）。部门下如果还有钉钉成员，钉钉会拒绝删除。</summary>
    Task DeleteDepartmentAsync(long dingTalkDeptId, CancellationToken ct = default);
}
