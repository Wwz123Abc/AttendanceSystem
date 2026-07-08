using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 考勤组的审批人名单（对应数据库表 AttendanceGroupApprover）：
/// 一个考勤组可以配多个审批人（同级、地位平等），员工提交申请时从这份名单里选一个人来审批自己的申请。
/// </summary>
[Table("AttendanceGroupApprover")]
public class AttendanceGroupApprover
{
    [Key] public int Id { get; set; }                       // 主键

    public int AttendanceGroupId { get; set; }              // 属于哪个考勤组
    public int UserId { get; set; }                         // 审批人是谁

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("AttendanceGroupId")]
    public AttendanceGroup AttendanceGroup { get; set; } = null!;   // 所属考勤组

    [ForeignKey("UserId")]
    public User Approver { get; set; } = null!;                     // 审批人（也是一个 User）
}
