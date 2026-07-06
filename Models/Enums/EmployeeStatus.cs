namespace AttendanceSystem.Models.Enums;

// 员工状态：把“在职 / 已停用 / 黑名单”这三种情况用有意义的名字列出来。
// 说明：数据库里其实只存了两个开关字段——IsActive(是否在职)、IsBlacklisted(是否黑名单)，
//       这个枚举是由那两个字段推导出来的，只用于页面展示和筛选，不单独存一列。

/// <summary>员工状态（用于展示与筛选）。</summary>
public enum EmployeeStatus
{
    Active      = 0,  // 在职（正常，可登录）
    Disabled    = 1,  // 已停用（禁止登录，但没拉黑，可再启用）
    Blacklisted = 2   // 黑名单（禁止登录，且标记为“永不录用”）
}

/// <summary>EmployeeStatus 的辅助方法。</summary>
public static class EmployeeStatusExtensions
{
    /// <summary>把状态转成中文名，给页面显示用。</summary>
    public static string ToDisplayName(this EmployeeStatus s) => s switch
    {
        EmployeeStatus.Active      => "在职",
        EmployeeStatus.Disabled    => "已停用",
        EmployeeStatus.Blacklisted => "黑名单",
        _                          => "未知"
    };
}
