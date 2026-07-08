using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 考勤组的打卡地点（对应数据库表 AttendanceGroupLocation）：
/// 一个考勤组可以配多个打卡地点（比如总部大楼、分部仓库各算一个），
/// 员工打卡时只要落在其中任意一个地点的有效半径内就算定位通过。
/// </summary>
[Table("AttendanceGroupLocation")]
public class AttendanceGroupLocation
{
    [Key] public int Id { get; set; }                       // 主键

    public int AttendanceGroupId { get; set; }              // 属于哪个考勤组

    [MaxLength(200)]
    public string? LocationName { get; set; }               // 地点名称（如"总部大楼"）

    public double Latitude  { get; set; }                   // 纬度
    public double Longitude { get; set; }                   // 经度

    /// <summary>这个地点的有效打卡半径（米）</summary>
    public int RadiusMeters { get; set; } = 500;

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("AttendanceGroupId")]
    public AttendanceGroup AttendanceGroup { get; set; } = null!;   // 所属考勤组
}
