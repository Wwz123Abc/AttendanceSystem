using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// "谁需要考勤"的统一规则（2026-10-07 业务确认）：系统是给临时工用的，只有角色是"普通员工"的人需要考勤；
/// 管理员、文员、主管、班组长都是正式工，一律免考勤——不打卡、不记旷工、不进看板和报表。
/// 角色是普通员工、但在员工管理里勾了"免考勤"的，同样不考勤。
/// 以前"免考勤"只能靠逐个勾选，管理员/文员/主管/班组长需要一个个勾，漏勾的人会被记旷工、进报表。
/// </summary>
public static class UserAttendanceExtensions
{
    /// <summary>这个人是否免考勤（正式工角色，或者勾了"免考勤"）。</summary>
    public static bool IsExemptFromAttendance(this User user) =>
        user.IsAttendanceExempt || user.Role != UserRole.Employee;

    /// <summary>只保留需要考勤的人（角色是普通员工，且没勾"免考勤"）。可以直接用在数据库查询里。</summary>
    public static IQueryable<User> NeedingAttendance(this IQueryable<User> users) =>
        users.Where(u => u.Role == UserRole.Employee && !u.IsAttendanceExempt);
}
