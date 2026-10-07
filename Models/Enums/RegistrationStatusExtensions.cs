namespace AttendanceSystem.Models.Enums;

/// <summary>RegistrationStatus 的辅助方法。</summary>
public static class RegistrationStatusExtensions
{
    /// <summary>把状态转成中文名，给页面显示用。</summary>
    public static string ToDisplayName(this RegistrationStatus s) => s switch
    {
        RegistrationStatus.Pending   => "待确认",
        RegistrationStatus.Confirmed => "已确认",
        RegistrationStatus.Rejected  => "已驳回",
        _                            => "未知"
    };
}
