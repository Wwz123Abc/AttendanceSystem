using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>用户/账号业务服务契约。</summary>
public interface IUserService
{
    /// <summary>校验工号+密码：成功返回用户并更新最后登录时间；工号/密码错误返回 null；账号已停用则抛 <see cref="InvalidOperationException"/>。</summary>
    Task<User?>   ValidateLoginAsync(string employeeNo, string password);
    /// <summary>
    /// 创建员工（工号唯一），自动对初始密码做哈希。
    /// 如果这个员工填了手机号、且所在部门能换算出对应的钉钉部门编号（部门是从钉钉导入/关联过来的），
    /// 会顺带在钉钉通讯录里也建一个新账号，并把钉钉分配的 userid 存回本地；
    /// 缺手机号或部门换算不出钉钉编号（多数手工建的临时工是这种情况）就直接跳过，不算错误；
    /// 已经具备条件但钉钉那边建失败了，才会在 DingTalkWarning 里带一句提示，不会拦住本地创建。
    /// </summary>
    Task<(User User, string? DingTalkWarning)> CreateUserAsync(User user, string plainPassword);
    /// <summary>修改密码（需校验原密码）。</summary>
    Task<bool>    ChangePasswordAsync(int userId, string oldPassword, string newPassword);
    /// <summary>
    /// 重置密码并返回明文（供管理员告知员工）。
    /// <paramref name="newPassword"/> 留空/空白则随机生成；指定了就用管理员输入的密码（至少 6 位）。
    /// </summary>
    Task<string>  ResetPasswordAsync(int userId, string? newPassword = null);
    /// <summary>
    /// 更新员工基本信息（不含密码）。
    /// 如果该员工绑定了钉钉（DingTalkUserId），会同时把姓名/手机号/工号/职位同步更新到钉钉通讯录；
    /// 钉钉这边同步失败不会拦住本地更新，只会在返回的 DingTalkWarning 里带一句提示。
    /// </summary>
    Task<(bool Success, string? DingTalkWarning)> UpdateUserAsync(User user);
    /// <summary>
    /// 停用员工（离职）：IsActive=false，禁止登录，但保留记录可查询。
    /// 如果该员工绑定了钉钉（DingTalkUserId），会同时调用钉钉接口把他从企业通讯录里删掉（本地记录仍保留）；
    /// 钉钉这边删除失败不会拦住本地停用，只会在返回的 DingTalkWarning 里带一句提示。
    /// </summary>
    Task<(bool Success, string? DingTalkWarning)> DeactivateUserAsync(int userId);
    /// <summary>重新启用员工（IsActive=true）。黑名单员工需先移出黑名单。</summary>
    Task<bool>    ActivateUserAsync(int userId);
    /// <summary>拉黑员工（标记永不录用并禁止登录）。</summary>
    Task<bool>    BlacklistUserAsync(int userId);
    /// <summary>移出黑名单（仍为已停用，需再手动启用）。</summary>
    Task<bool>    RemoveFromBlacklistAsync(int userId);
    /// <summary>
    /// 彻底删除员工（级联删除其考勤/审批/通知等数据）。
    /// 如果该员工绑定了钉钉（DingTalkUserId），会同时调用钉钉接口把他从企业通讯录里删掉；
    /// 钉钉这边删除失败不会拦住本地删除，只会在返回的 DingTalkWarning 里带一句提示。
    /// </summary>
    Task<(bool Success, string? DingTalkWarning)> DeleteUserAsync(int userId);
    /// <summary>批量启用/停用（启用会跳过黑名单员工），返回实际处理条数。</summary>
    Task<int>     SetActiveBatchAsync(IEnumerable<int> userIds, bool active);
    /// <summary>按部门/考勤组/角色/状态/关键字分页查询员工。</summary>
    Task<(List<User> Users, int Total)> GetUsersAsync(
        int? deptId = null, int? groupId = null, UserRole? role = null,
        string? keyword = null, int pageIndex = 1, int pageSize = 20,
        EmployeeStatus? status = null, bool unassignedOnly = false);
    /// <summary>按 Id 获取员工（不含导航属性）。</summary>
    Task<User?>   GetUserByIdAsync(int userId);
    /// <summary>按 Id 获取员工（含部门/考勤组/上级）。</summary>
    Task<User?>   GetUserWithDetailsAsync(int userId);
    /// <summary>判断工号是否已存在（可排除指定用户，用于更新校验）。</summary>
    Task<bool>    IsEmployeeNoExistsAsync(string employeeNo, int? excludeUserId = null);
    /// <summary>
    /// 按部门自动生成下一个工号（前缀+5位流水号，如 IN00001）。
    /// 从该部门往上找最近的一个"公司"节点（深圳GA/科瑞科技/成都鹰诺/鼎力/新能源），
    /// 找不到匹配的公司则返回 null（表示不自动生成，改回手动填写）。
    /// </summary>
    Task<string?> GenerateNextEmployeeNoAsync(int? departmentId);
}
