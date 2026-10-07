namespace AttendanceSystem.Models.Enums;

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
