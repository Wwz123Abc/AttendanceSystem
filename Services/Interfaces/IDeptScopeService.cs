using AttendanceSystem.Middlewares;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 部门范围解析：把"当前登录管理员的 ScopedDepartmentId"解析成一份"允许看到/管到的部门 id 全集"。
/// 所有 ManagePolicy/ApprovePolicy 页面/接口都应该用这个，不要各自重新实现部门子树遍历逻辑。
/// </summary>
public interface IDeptScopeService
{
    /// <summary>取某个部门自己 + 所有下级部门的 id 集合。</summary>
    Task<HashSet<int>> GetSubtreeIdsAsync(int deptId);

    /// <summary>取当前用户"可见"的部门 id 全集：ScopedDepartmentId 为空 → 返回 null（表示不受限，
    /// 调用方应该跳过部门过滤，按全公司查）；有值 → 返回该部门 + 下级部门 id 集合。</summary>
    Task<HashSet<int>?> GetVisibleDeptIdsAsync(CurrentUser currentUser);

    /// <summary>把调用方传入的单个 deptId 查询参数，收窄到当前用户实际被允许看的范围：
    /// 不受限用户原样返回调用方传入的值（可能是 null=看全部，也可能是主动选了某个部门筛选）；
    /// 受限用户忽略/校验调用方传入的 deptId——如果不在自己范围内就强制收窄成自己的 ScopedDepartmentId，
    /// 调用方没传就直接用自己的 ScopedDepartmentId。返回值可以直接喂给 GetUsersAsync(deptId:) 这类方法。</summary>
    Task<int?> ResolveEffectiveDeptIdAsync(CurrentUser currentUser, int? requestedDeptId);

    /// <summary>同上，但用于多选场景（比如月度报表按部门批量导出）：取"调用方请求的部门集合"和
    /// "当前用户可见范围"的交集；不受限用户原样返回请求集合（null 表示没传、看全部）。</summary>
    Task<List<int>?> ResolveEffectiveDeptIdsAsync(CurrentUser currentUser, List<int>? requestedDeptIds);

    /// <summary>是否可以访问某个具体部门（用于单条记录级别的校验，例如"能不能把这个员工挪到这个部门"）。
    /// deptId 为 null（比如员工没分配部门）时，只有不受限用户能访问。</summary>
    Task<bool> CanAccessDeptAsync(CurrentUser currentUser, int? deptId);
}
