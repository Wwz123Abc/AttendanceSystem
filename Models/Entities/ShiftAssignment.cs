using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 员工排班记录（对应数据库表 ShiftAssignment）：某员工某一天用哪个班次。
/// </summary>
[Table("ShiftAssignment")]
public class ShiftAssignment
{
    [Key] public int Id { get; set; }                       // 主键

    public int UserId          { get; set; }                // 哪个员工
    public int ShiftScheduleId { get; set; }                // 用哪个班次

    /// <summary>排班日期</summary>
    public DateOnly WorkDate { get; set; }

    /// <summary>是否由系统自动排班（false=人工排的）</summary>
    public bool IsAutoAssigned { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 创建时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;                 // 对应的员工

    [ForeignKey("ShiftScheduleId")]
    public ShiftSchedule ShiftSchedule { get; set; } = null!;  // 对应的班次
}
