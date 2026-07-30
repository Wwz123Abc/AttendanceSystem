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
    /// =All 或 =DirectReports 这两种范围不需要用到，留空。
    /// </summary>
    public int? ScopeId { get; set; }

    /// <summary>是否有效：撤下时置为 false，不做物理删除（保留发布历史和已读记录）。</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 发布时间
    public DateTime UpdatedAt { get; set; } = DateTime.Now; // 最后修改时间（撤下时也会更新）

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("PublisherUserId")]
    public User Publisher { get; set; } = null!;                        // 发布人

    public ICollection<AnnouncementRead> Reads { get; set; } = [];      // 受众名单 + 已读记录
}

/// <summary>
/// 公告的已读记录（对应数据库表 AnnouncementRead）：公告发布那一刻，按算出来的受众名单
/// 逐人建一行（ReadAt 留空）；员工打开这条公告详情时把 ReadAt 填上，后台就能看到谁读了谁没读。
/// </summary>
[Table("AnnouncementRead")]
public class AnnouncementRead
{
    [Key] public int Id { get; set; }                       // 主键

    public int AnnouncementId { get; set; }                 // 哪条公告
    public int UserId { get; set; }                         // 受众里的哪个人

    public DateTime? ReadAt { get; set; }                   // 读了的时间；没读则为空

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("AnnouncementId")]
    public Announcement Announcement { get; set; } = null!;

    [ForeignKey("UserId")]
    public User User { get; set; } = null!;
}
