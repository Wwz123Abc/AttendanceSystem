using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

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
