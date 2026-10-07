using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 原始打卡流水（对应数据库表 AttendancePunch）。
/// 每“打一次卡”就存一条：上班一条、下班一条，记录精确到分钟。
/// </summary>
[Table("AttendancePunch")]
public class AttendancePunch
{
    [Key] public int Id { get; set; }                       // 主键

    public int UserId { get; set; }                         // 哪个员工打的

    /// <summary>打卡时间（精确到分钟）</summary>
    public DateTime PunchTime { get; set; }

    /// <summary>上班 / 下班</summary>
    public PunchType PunchType { get; set; }

    public double? Latitude  { get; set; }                  // 打卡地点纬度
    public double? Longitude { get; set; }                  // 打卡地点经度

    [MaxLength(500)]
    public string? Address { get; set; }                    // 打卡地点文字地址

    [MaxLength(200)]
    public string? DeviceInfo { get; set; }                 // 打卡设备信息（钉钉同步的会标 DingTalk:xxx）

    /// <summary>是否有效（补卡审批通过后，原来的无效记录会标为 false）</summary>
    public bool IsValid { get; set; } = true;

    /// <summary>
    /// 定位是否有效：null=未做定位校验（考勤组没开定位打卡，或没有配置地点）；
    /// true=落在考勤组某个打卡地点的有效半径内；false=离所有配置地点都太远。
    /// </summary>
    public bool? LocationValid { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 入库时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;                 // 对应的员工
}
