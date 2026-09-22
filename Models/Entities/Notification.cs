using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 系统通知（对应数据库表 Notification）：发给某个员工的站内消息。
/// 比如“今日旷工提醒”“审批已通过”。
/// </summary>
[Table("Notification")]
public class Notification
{
    [Key] public int Id { get; set; }                       // 主键

    public int UserId { get; set; }                         // 发给哪个员工

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;       // 标题

    [Required, MaxLength(2000)]
    public string Content { get; set; } = string.Empty;     // 正文内容

    /// <summary>通知类型：PunchReminder（打卡提醒）/ ApprovalPending（待审批）/ ApprovalResult（审批结果）</summary>
    [MaxLength(50)]
    public string NotificationType { get; set; } = string.Empty;

    /// <summary>关联的业务编号（如对应的审批申请 Id），方便点通知跳转</summary>
    public int? RelatedId { get; set; }

    public bool IsRead { get; set; } = false;               // 是否已读

    public DateTime  CreatedAt { get; set; } = DateTime.Now; // 创建时间
    public DateTime? ReadAt    { get; set; }                 // 读了的时间（没读则为空）

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;                 // 收到通知的员工
}
