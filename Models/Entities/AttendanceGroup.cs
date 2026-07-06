using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 考勤组（对应数据库表 AttendanceGroup）。
/// 把一批员工归在一组，统一管打卡规则：打卡地点、定位范围、午休/晚餐扣时等。
/// </summary>
[Table("AttendanceGroup")]
public class AttendanceGroup
{
    [Key] public int Id { get; set; }                       // 主键：考勤组唯一编号

    /// <summary>考勤组名称</summary>
    [Required, MaxLength(100)]
    public string GroupName { get; set; } = string.Empty;

    /// <summary>所属公司</summary>
    [MaxLength(100)]
    public string? CompanyName { get; set; }

    /// <summary>对应的部门编号（系统按部门自动建的考勤组会填这个；手动建的组为空）。用于“考勤组跟随部门”。</summary>
    public int? DepartmentId { get; set; }

    /// <summary>负责该组的文员用户编号</summary>
    public int? ClerkUserId { get; set; }

    /// <summary>定位打卡的有效半径（米）：超出这个范围打卡视为无效</summary>
    public int PunchRadiusMeters { get; set; } = 500;

    public double? LocationLatitude  { get; set; }          // 打卡点纬度
    public double? LocationLongitude { get; set; }          // 打卡点经度

    [MaxLength(200)]
    public string? LocationName { get; set; }               // 打卡点名称

    /// <summary>是否启用定位打卡（开启后会校验打卡距离）</summary>
    public bool EnableLocationPunch { get; set; } = false;

    /// <summary>午休时长（分钟）：算工时时自动扣掉</summary>
    public int LunchBreakMinutes { get; set; } = 60;

    /// <summary>晚餐时长（分钟）：算工时时自动扣掉</summary>
    public int DinnerBreakMinutes { get; set; } = 30;

    public bool IsActive { get; set; } = true;              // 是否启用

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 创建时间
    public DateTime UpdatedAt { get; set; } = DateTime.Now; // 最后修改时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    public ICollection<User>          Users          { get; set; } = [];  // 本组的员工
    public ICollection<ShiftSchedule> ShiftSchedules { get; set; } = [];  // 本组的班次
    public ICollection<ApprovalFlow>  ApprovalFlows  { get; set; } = [];  // 本组的审批流配置
}
