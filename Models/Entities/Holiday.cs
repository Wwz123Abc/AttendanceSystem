using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 假期设置（对应数据库表 Holiday）：法定节假日 / 公司休息日 / 调班补班日。
/// 用来决定某天“要不要上班、算不算应出勤”。
/// </summary>
[Table("Holiday")]
public class Holiday
{
    [Key] public int Id { get; set; }                       // 主键

    [Required, MaxLength(100)]
    public string HolidayName { get; set; } = string.Empty; // 假期名称（如“国庆节”）

    public DateOnly HolidayDate { get; set; }               // 具体哪一天

    public HolidayType HolidayType { get; set; }            // 类型：法定/公司休息/调班补班

    /// <summary>适用范围：空 = 全公司；有值 = 只对指定考勤组生效</summary>
    public int? AttendanceGroupId { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }                // 备注

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 创建时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("AttendanceGroupId")]
    public AttendanceGroup? AttendanceGroup { get; set; }   // 适用的考勤组（为空表示全公司）
}
