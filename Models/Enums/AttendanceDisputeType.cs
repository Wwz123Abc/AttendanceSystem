namespace AttendanceSystem.Models.Enums;

/// <summary>
/// 员工对某天考勤记录提的异议类型（考勤核对功能，2026-11-03 新增）。
/// 员工点"有异议"时选一个，方便管理员/文员快速判断该去补卡还是查加班。
/// </summary>
public enum AttendanceDisputeType
{
    /// <summary>漏打卡 / 那天没有记录。</summary>
    MissingPunch = 1,

    /// <summary>正班工时不对（多算或少算）。</summary>
    WorkHoursWrong = 2,

    /// <summary>状态不对（迟到/早退/旷工/请假等）。</summary>
    StatusWrong = 3,

    /// <summary>加班没算上、加班小时不对（含"加班单还没审批"的疑问）。</summary>
    OvertimeWrong = 4,

    /// <summary>其他。</summary>
    Other = 9
}

/// <summary>异议状态（提交 → 处理/驳回/撤回）。</summary>
public enum AttendanceDisputeStatus
{
    /// <summary>待处理（刚提交）。</summary>
    Pending = 1,

    /// <summary>处理中（管理员/文员已接手）。</summary>
    Processing = 2,

    /// <summary>已处理（已解释或已调整考勤）。</summary>
    Resolved = 3,

    /// <summary>已驳回（经核对记录无误）。</summary>
    Rejected = 4,

    /// <summary>员工自己撤回。</summary>
    Cancelled = 5
}

/// <summary>枚举的中文显示文字（页面/通知共用，避免各处各写一份）。</summary>
public static class AttendanceDisputeTexts
{
    public static string Text(this AttendanceDisputeType t) => t switch
    {
        AttendanceDisputeType.MissingPunch   => "漏打卡/没有记录",
        AttendanceDisputeType.WorkHoursWrong => "正班工时不对",
        AttendanceDisputeType.StatusWrong    => "状态不对（迟到/早退/旷工/请假）",
        AttendanceDisputeType.OvertimeWrong  => "加班没算上/加班小时不对",
        _                                    => "其他"
    };

    public static string Text(this AttendanceDisputeStatus s) => s switch
    {
        AttendanceDisputeStatus.Pending    => "待处理",
        AttendanceDisputeStatus.Processing => "处理中",
        AttendanceDisputeStatus.Resolved   => "已处理",
        AttendanceDisputeStatus.Rejected   => "已驳回",
        _                                  => "已撤回"
    };

    /// <summary>状态对应的徽标样式（Bootstrap）。</summary>
    public static string CssClass(this AttendanceDisputeStatus s) => s switch
    {
        AttendanceDisputeStatus.Pending    => "bg-warning text-dark",
        AttendanceDisputeStatus.Processing => "bg-info text-dark",
        AttendanceDisputeStatus.Resolved   => "bg-success",
        AttendanceDisputeStatus.Rejected   => "bg-secondary",
        _                                  => "bg-light text-dark border"
    };
}
