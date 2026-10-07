namespace AttendanceSystem.Models.Enums;

// 下面是一个「扩展方法」：相当于给上面的 UserRole 增加一个随手可用的小工具，
// 作用是把英文角色名翻译成中文，给网页显示用。
/// <summary>UserRole 的辅助方法。</summary>
public static class UserRoleExtensions
{
    /// <summary>把角色（如 Admin）转换成中文名（如「管理员」），用于页面展示。</summary>
    public static string ToDisplayName(this UserRole role) => role switch
    {
        UserRole.Admin      => "管理员",
        UserRole.Clerk      => "文员",
        UserRole.Supervisor => "主管",
        UserRole.TeamLeader => "班组长",
        _                   => "员工"   // 其余情况（也就是 Employee）一律显示「员工」
    };
}
