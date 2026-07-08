namespace AttendanceSystem.Models.Enums;

// 员工自助登记状态：员工扫码填完基础信息后，先进"待确认"，等管理员审核。

/// <summary>员工自助登记（扫码登记）的处理状态。</summary>
public enum RegistrationStatus
{
    Pending   = 1,  // 待确认：员工已提交，管理员还没处理
    Confirmed = 2,  // 已确认：管理员补全部门/工号等信息后，正式建了账号
    Rejected  = 3   // 已驳回：管理员认为这条登记有问题，不予录入
}

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
