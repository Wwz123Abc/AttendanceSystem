using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 系统公告（对应数据库表 Announcement）：管理员/文员/主管/班组长发给员工看的公告，
/// 显示在员工的"公告栏"里。撤下走软删除（IsActive=false），不做物理删除，保留发布历史和已读记录。
/// </summary>
[Table("Announcement")]
public class Announcement
{
    [Key] public int Id { get; set; }                       // 主键

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;       // 标题

    [Required, MaxLength(2000)]
    public string Content { get; set; } = string.Empty;     // 正文

    public int PublisherUserId { get; set; }                // 发布人

    /// <summary>发给谁：全公司 / 指定部门 / 指定考勤组 / 发布人自己的直属下属</summary>
    public AnnouncementScopeType ScopeType { get; set; }

    /// <summary>
    /// 配合 ScopeType 用：ScopeType=Department 时是部门 Id，=AttendanceGroup 时是考勤组 Id；
    /// =All、=DirectReports、=Role 这三种范围不需要用到，留空。
    /// </summary>
    public int? ScopeId { get; set; }

    /// <summary>ScopeType=Role 时用：选中的角色，逗号分隔的 UserRole 数字（如"1,2"=管理员+文员）。
    /// 其它 ScopeType 留空。跟 ShiftSchedule.RestDaysOfWeek 一样的存法，不用建关联表。</summary>
    [MaxLength(50)]
    public string? ScopeRoles { get; set; }

    /// <summary>是否有效：撤下时置为 false，不做物理删除（保留发布历史和已读记录）。</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 发布时间
    public DateTime UpdatedAt { get; set; } = DateTime.Now; // 最后修改时间（撤下时也会更新）

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("PublisherUserId")]
    public User Publisher { get; set; } = null!;                        // 发布人

    public ICollection<AnnouncementRead> Reads { get; set; } = [];      // 受众名单 + 已读记录
}
