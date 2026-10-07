namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 通知类型常量：存进 <see cref="Notification.NotificationType"/> 字段的取值。
/// 以前这些字符串在好几个文件里各写一遍，拼错一个字就查不到通知；现在统一引用这里。
/// 注意：值已经存在数据库里，不能改；前端（_Layout.cshtml 的通知轮询）按这些字符串判断跳转，也要同步。
/// </summary>
public static class NotificationTypes
{
    /// <summary>打卡提醒。</summary>
    public const string PunchReminder = "PunchReminder";

    /// <summary>待审批（新单、审批流转、待审批汇总提醒）。</summary>
    public const string ApprovalPending = "ApprovalPending";

    /// <summary>审批结果（通过/驳回/撤销）。</summary>
    public const string ApprovalResult = "ApprovalResult";

    /// <summary>公告。</summary>
    public const string Announcement = "Announcement";
}
