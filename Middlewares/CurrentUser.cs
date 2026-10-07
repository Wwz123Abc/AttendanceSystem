using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Middlewares;

/// <summary>当前登录用户的信息（从 Cookie 里的身份标签解析出来，供页面使用）。</summary>
public sealed class CurrentUser
{
    public int      UserId            { get; init; }                    // 用户编号
    public string   EmployeeNo        { get; init; } = string.Empty;    // 工号
    public string   RealName          { get; init; } = string.Empty;    // 姓名
    public UserRole Role              { get; init; }                    // 角色
    public int?     AttendanceGroupId { get; init; }                    // 所属考勤组
    public int?     DepartmentId      { get; init; }                    // 所属部门
    public string?  ContractCompany   { get; init; }                    // 劳务公司（合同公司，用于页面水印）

    /// <summary>范围限定部门（仅管理员/文员有意义）：为空 = 不受限（总部超级管理员）；有值 = 只能看/管
    /// 这个部门及其下级范围内的数据（"分公司管理员"）。故意每次请求都从数据库读（不放进登录 Cookie 的
    /// claim），这样总部管理员改了某人的范围/把某人重新设成不受限，对方下一个请求就立刻生效，
    /// 不用等到重新登录——跟 IsActive 停用踢线是同一个"每请求校验一次"的道理，这里本来就已经查库了，
    /// 顺手带上这个字段幂等零成本。</summary>
    public int?     ScopedDepartmentId { get; init; }

    // 下面几个是便捷判断（页面里直接用，不用每次写一长串条件）
    public bool IsAdmin    => Role == UserRole.Admin;   // 是不是管理员
    public bool IsClerk    => Role == UserRole.Clerk;   // 是不是文员
    public bool CanApprove => Role is UserRole.Admin or UserRole.Clerk  // 有没有审批权限
                                   or UserRole.Supervisor or UserRole.TeamLeader;
    public bool IsScoped   => ScopedDepartmentId.HasValue;   // 是不是"分公司管理员"（受部门范围限制）

    /// <summary>是不是"总部超级管理员"：角色为 Admin，且自己没有被设置管理范围。</summary>
    public bool IsHqSuperAdmin => Role == UserRole.Admin && !IsScoped;

    /// <summary>
    /// 角色层级：能不能操作（编辑/停用/拉黑/删除/重置密码）这个目标账号。前提是目标已经在自己的部门范围内——
    /// 光看部门范围不够：总部管理员的 DepartmentId 常常挂在某个分公司下，这个分公司的文员/分公司管理员
    /// "管得到"他，重置密码就能接管总部账号。规则：总部超级管理员可以操作所有人；其他人不能动
    /// "总部管理员"（Admin 且没设范围），文员也不能动任何管理员；分公司管理员之间维持可操作。
    /// </summary>
    public bool CanManageAccount(UserRole targetRole, int? targetScopedDepartmentId) =>
        CanManageAccountCore(Role, ScopedDepartmentId, targetRole, targetScopedDepartmentId);

    /// <summary>角色层级判断的核心公式，抽成静态方法：页面用 <see cref="CanManageAccount"/>（基于当前登录者），
    /// <c>UserService</c> 内部也调这同一个公式（从数据库现读操作者/目标的角色和范围，不用先拼一个完整的
    /// <see cref="CurrentUser"/>）。以前只有这一个类有这份判断，服务层完全不知道"操作者是谁"，
    /// 智能助手（AGENT）绕过页面直接调用 <c>UserService</c> 的写方法，文员因此能通过助手重置/删除
    /// 总部超级管理员的账号（2026-09-29 审查发现，S1，严重）。现在两处共用同一份公式，不会再各写一遍、
    /// 也不会再有调用方漏掉这道检查。</summary>
    public static bool CanManageAccountCore(UserRole actingRole, int? actingScopedDepartmentId, UserRole targetRole, int? targetScopedDepartmentId)
    {
        var actingIsHqSuperAdmin = actingRole == UserRole.Admin && actingScopedDepartmentId is null;
        if (actingIsHqSuperAdmin) return true;
        if (targetRole == UserRole.Admin)
            return targetScopedDepartmentId.HasValue && actingRole == UserRole.Admin;
        // 范围为空的文员=能看/管全公司的"总部文员"：受范围限制的账号（分公司管理员/分公司文员）不能操作他，
        // 不然重置他的密码就能接管一个不受限的账号（2026-09-24 第 11 轮审查，用户确认禁止）。总部文员之间不受影响
        return !(actingScopedDepartmentId is not null && targetRole == UserRole.Clerk && targetScopedDepartmentId is null);
    }
}
