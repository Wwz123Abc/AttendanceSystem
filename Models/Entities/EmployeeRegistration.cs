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

    /// <summary>岗位（员工自己从固定选项里选，如"电气熟手""普工"等，管理员确认时可再调整）。</summary>
    [MaxLength(100)]
    public string? Position { get; set; }

    /// <summary>劳务公司（对应正式员工账号上的"合同公司"字段，即实际签合同、发工资的公司主体）。</summary>
    [MaxLength(100)]
    public string? ContractCompany { get; set; }

    /// <summary>家庭住址</summary>
    [MaxLength(200)]
    public string? HomeAddress { get; set; }

    /// <summary>紧急联系人姓名（不是本人，出意外时用来联系家属/朋友）</summary>
    [MaxLength(50)]
    public string? EmergencyContactName { get; set; }

    /// <summary>紧急联系人电话</summary>
    [MaxLength(20)]
    public string? EmergencyContactPhone { get; set; }

    /// <summary>身份证照片的访问地址（员工提交登记时上传，确认建号时会带到正式员工资料里）。</summary>
    [MaxLength(500)]
    public string? IdCardPhotoUrl { get; set; }

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
