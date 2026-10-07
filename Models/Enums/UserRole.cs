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
