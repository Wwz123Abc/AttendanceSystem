namespace AttendanceSystem.Models.Enums;

// 本文件集中放“和审批有关的固定选项”。名字后面的数字是存进数据库的编号。

/// <summary>审批类型：员工提交的是哪种申请。</summary>
public enum ApprovalType
{
    PunchReplenishment = 1,  // 补卡：忘打卡了，事后申请补上
    Leave              = 2,  // 请假
    Overtime           = 3,  // 加班
    BusinessTrip       = 4   // 出差：审批通过后，出差期间自动算全勤，不用打卡
}

/// <summary>审批状态：一张申请单当前走到哪一步。</summary>
public enum ApprovalStatus
{
    Pending    = 1,  // 待审批：刚提交，等第一个人审
    InProgress = 2,  // 审批中：多级审批里，前面通过了、还没走完
    Approved   = 3,  // 已通过：全部审批人都同意
    Rejected   = 4,  // 已驳回：被某个审批人否决
    Cancelled  = 5   // 已撤销：申请人自己撤回了
}

/// <summary>考勤组的审批层级：一个考勤组的申请要走一级还是二级审批。</summary>
public enum ApprovalLevelType
{
    Level1 = 1,  // 一级审批：只需要班组长审批
    Level2 = 2   // 二级审批：班组长审批通过后，再由申请人的直属上级（主管）审批
}

/// <summary>请假类型：请的是哪种假。</summary>
public enum LeaveType
{
    PersonalLeave     = 1,  // 事假
    SickLeave         = 2,  // 病假
    AnnualLeave       = 3,  // 年假
    MarriageLeave     = 4,  // 婚假
    MaternityLeave    = 5,  // 产假
    BereavementLeave  = 6,  // 丧假
    CompensatoryLeave = 7   // 调休
}

// 扩展方法：给 ApprovalType 增加一个把英文翻译成中文的小工具，供页面/通知文案使用。
/// <summary>ApprovalType 的辅助方法。</summary>
public static class ApprovalTypeExtensions
{
    /// <summary>把审批类型（如 Leave）转成中文名（如「请假」）。</summary>
    public static string ToDisplayName(this ApprovalType type) => type switch
    {
        ApprovalType.PunchReplenishment => "补卡",
        ApprovalType.Leave              => "请假",
        ApprovalType.Overtime           => "加班",
        ApprovalType.BusinessTrip       => "出差",
        _                               => "其他"   // 兜底，正常不会走到这里
    };
}

/// <summary>ApprovalLevelType 的辅助方法。</summary>
public static class ApprovalLevelTypeExtensions
{
    /// <summary>把审批层级转成中文名，供页面显示用。</summary>
    public static string ToDisplayName(this ApprovalLevelType level) => level switch
    {
        ApprovalLevelType.Level1 => "一级审批（班组长）",
        ApprovalLevelType.Level2 => "二级审批（班组长 + 直属上级）",
        _                        => "一级审批（班组长）"
    };
}
