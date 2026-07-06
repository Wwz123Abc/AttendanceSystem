namespace AttendanceSystem.Models.Enums;

// 「枚举」= 把一组固定的选项，用有意义的英文名字一一列出来，方便程序使用和人阅读。
// 下面列出系统里的 5 种用户身份（角色）。每个名字后面的数字，是它存到数据库时用的编号。

/// <summary>用户角色：决定一个人登录后在系统里能做哪些事。</summary>
public enum UserRole
{
    Admin      = 1,  // 管理员：权限最大，所有功能都能用
    Clerk      = 2,  // 文员：管理自己所在考勤组的员工和考勤数据
    Supervisor = 3,  // 主管：可以审批下属的请假 / 补卡 / 加班
    TeamLeader = 4,  // 班组长：和主管类似，也有审批权限
    Employee   = 5   // 普通员工：只能打卡、查看自己的考勤、提交申请
}

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
