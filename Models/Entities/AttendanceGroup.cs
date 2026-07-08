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

    /// <summary>
    /// 所属公司（展示用）：不再手动填写，由"所属部门"里各部门的所属公司自动派生
    /// （勾了几个部门就取这几个部门的公司名去重拼起来）。
    /// </summary>
    [MaxLength(100)]
    public string? CompanyName { get; set; }

    /// <summary>负责该组的文员用户编号</summary>
    public int? ClerkUserId { get; set; }

    /// <summary>是否启用定位打卡（开启后，打卡要落在下面 Locations 里任意一个地点的有效半径内才算数）</summary>
    public bool EnableLocationPunch { get; set; } = false;

    /// <summary>午休时长（分钟）：算工时时自动扣掉</summary>
    public int LunchBreakMinutes { get; set; } = 60;

    /// <summary>晚餐时长（分钟）：算工时时自动扣掉</summary>
    public int DinnerBreakMinutes { get; set; } = 30;

    public bool IsActive { get; set; } = true;              // 是否启用

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 创建时间
    public DateTime UpdatedAt { get; set; } = DateTime.Now; // 最后修改时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    public ICollection<User>                    Users          { get; set; } = [];  // 本组的员工
    public ICollection<ShiftSchedule>           ShiftSchedules { get; set; } = [];  // 本组的班次
    public ICollection<AttendanceGroupApprover> Approvers      { get; set; } = [];  // 本组的审批人名单
    public ICollection<AttendanceGroupLocation> Locations      { get; set; } = [];  // 本组的打卡地点（可以多个）
    public ICollection<Department>              Departments    { get; set; } = [];  // 长期跟随本组的部门（这些部门以后新增/调入的员工自动归入本组）
}
