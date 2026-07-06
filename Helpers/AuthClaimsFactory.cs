using System.Security.Claims;
using AttendanceSystem.Models.Entities;

namespace AttendanceSystem.Helpers;

// 「Claims（声明）」= 登录成功后写进 Cookie 的一组“身份小标签”，
// 记录“你是谁、工号、姓名、什么角色”。之后每次访问，系统靠它认出你。

/// <summary>
/// 登录身份标签（Claims）的制造工厂。
/// 网页登录和接口登录都调它，避免两处各写一份相同代码。
/// </summary>
public static class AuthClaimsFactory
{
    /// <summary>
    /// 根据用户信息，生成登录要写进 Cookie 的身份标签列表。
    /// 基础信息（编号/工号/姓名/角色）一定有；考勤组、部门有值时才追加。
    /// </summary>
    public static List<Claim> BuildUserClaims(User user)
    {
        // 先放 4 个必有的基础标签
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),   // 用户编号 Id
            new(ClaimTypes.Name,           user.EmployeeNo),      // 工号
            new("RealName",                user.RealName),        // 姓名
            new(ClaimTypes.Role,           user.Role.ToString())  // 角色
        };

        // 有考勤组就加上考勤组编号
        if (user.AttendanceGroupId.HasValue)
            claims.Add(new("AttendanceGroupId", user.AttendanceGroupId.Value.ToString()));

        // 有部门就加上部门编号
        if (user.DepartmentId.HasValue)
            claims.Add(new("DepartmentId", user.DepartmentId.Value.ToString()));

        return claims;
    }
}
