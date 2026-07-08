using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 员工自助登记（对应数据库表 EmployeeRegistration）：
/// 新员工扫二维码，自己填姓名/手机号/身份证号提交，先存在这里等管理员确认；
/// 管理员审核时补上部门、工号、考勤组等信息，才会正式建成 User 账号。
/// </summary>
[Table("EmployeeRegistration")]
public class EmployeeRegistration
{
    [Key] public int Id { get; set; }                       // 主键

    [Required, MaxLength(50)]
    public string RealName { get; set; } = string.Empty;    // 员工自己填的姓名

    [Required, MaxLength(20)]
    public string Phone { get; set; } = string.Empty;       // 员工自己填的手机号

    [Required, MaxLength(18)]
    public string IdNumber { get; set; } = string.Empty;    // 员工自己填的身份证号

    public RegistrationStatus Status { get; set; } = RegistrationStatus.Pending;  // 处理状态

    public DateTime SubmittedAt { get; set; } = DateTime.Now;   // 员工提交时间
    public DateTime? ReviewedAt { get; set; }                   // 管理员处理时间（还没处理则为空）

    /// <summary>确认通过后，对应生成的正式员工账号编号（驳回则一直为空）。</summary>
    public int? ConfirmedUserId { get; set; }

    /// <summary>驳回原因（驳回时可填，方便以后追溯为什么没通过）。</summary>
    [MaxLength(200)]
    public string? RejectReason { get; set; }

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("ConfirmedUserId")]
    public User? ConfirmedUser { get; set; }
}
