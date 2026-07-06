using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>用户/账号业务服务契约。</summary>
public interface IUserService
{
    /// <summary>校验工号+密码：成功返回用户并更新最后登录时间；工号/密码错误返回 null；账号已停用则抛 <see cref="InvalidOperationException"/>。</summary>
    Task<User?>   ValidateLoginAsync(string employeeNo, string password);
    /// <summary>创建员工（工号唯一），自动对初始密码做哈希。</summary>
    Task<User>    CreateUserAsync(User user, string plainPassword);
    /// <summary>修改密码（需校验原密码）。</summary>
    Task<bool>    ChangePasswordAsync(int userId, string oldPassword, string newPassword);
    /// <summary>
    /// 重置密码并返回明文（供管理员告知员工）。
    /// <paramref name="newPassword"/> 留空/空白则随机生成；指定了就用管理员输入的密码（至少 6 位）。
    /// </summary>
    Task<string>  ResetPasswordAsync(int userId, string? newPassword = null);
    /// <summary>更新员工基本信息（不含密码）。</summary>
    Task<bool>    UpdateUserAsync(User user);
    /// <summary>停用员工（IsActive=false，禁止登录，但保留记录可查询）。</summary>
    Task<bool>    DeactivateUserAsync(int userId);
    /// <summary>重新启用员工（IsActive=true）。黑名单员工需先移出黑名单。</summary>
    Task<bool>    ActivateUserAsync(int userId);
    /// <summary>拉黑员工（标记永不录用并禁止登录）。</summary>
    Task<bool>    BlacklistUserAsync(int userId);
    /// <summary>移出黑名单（仍为已停用，需再手动启用）。</summary>
    Task<bool>    RemoveFromBlacklistAsync(int userId);
    /// <summary>彻底删除员工（级联删除其考勤/审批/通知等数据）。</summary>
    Task<bool>    DeleteUserAsync(int userId);
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
}
