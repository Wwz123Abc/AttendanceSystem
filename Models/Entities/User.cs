using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

// 「实体类」= 用一个 C# 类对应数据库里的一张表，类的每个属性就是表的一个字段（列）。
// 几个常见标记的含义：
//   [Table("User")]  → 对应数据库表名叫 User
//   [Key]            → 这个字段是主键（每行的唯一编号）
//   [Required]       → 必填，不能为空
//   [MaxLength(50)]  → 这个文本最多 50 个字
//   int?  末尾的问号 → 表示“可以为空”
//   [ForeignKey]/导航属性 → 指向另一张表（即“这条记录关联的部门/考勤组”等）

/// <summary>员工 / 管理员账号（对应数据库表 User）。</summary>
[Table("User")]
public class User
{
    [Key] public int Id { get; set; }                       // 主键：每个员工的唯一编号

    /// <summary>工号（唯一，作为登录账号）</summary>
    [Required, MaxLength(50)]
    public string EmployeeNo { get; set; } = string.Empty;

    /// <summary>真实姓名</summary>
    [Required, MaxLength(50)]
    public string RealName { get; set; } = string.Empty;

    /// <summary>密码哈希（加密后的密码，永远不存明文）</summary>
    [Required, MaxLength(256)]
    public string PasswordHash { get; set; } = string.Empty;

    public int? DepartmentId { get; set; }                  // 所属部门的编号（可空）

    /// <summary>岗位名称</summary>
    [MaxLength(100)]
    public string? Position { get; set; }

    /// <summary>角色（决定权限大小）</summary>
    public UserRole Role { get; set; } = UserRole.Employee; // 默认是普通员工

    /// <summary>
    /// 用工性质（由角色推导，仅展示用，不单独存库）：
    /// 本系统里只有「管理员」是正式工，其余角色（文员/主管/班组长/员工）一律是临时工。
    /// </summary>
    [NotMapped]
    public string EmploymentTypeText => Role == UserRole.Admin ? "正式工" : "临时工";

    public int? AttendanceGroupId { get; set; }             // 所属考勤组的编号（可空）

    /// <summary>直属上级（审批流默认的第一个审批人）</summary>
    public int? SupervisorUserId { get; set; }

    [MaxLength(20)]
    public string? Phone { get; set; }                      // 手机号

    /// <summary>身份证号（18 位）。多来自员工扫码自助登记时自己填写。</summary>
    [MaxLength(18)]
    public string? IdNumber { get; set; }

    /// <summary>合同公司（员工实际签合同、发工资的公司主体，可能和所在部门的公司不是同一家）。</summary>
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

    /// <summary>身份证照片的访问地址（上传后存到 wwwroot/uploads 下，这里只存相对路径）</summary>
    [MaxLength(500)]
    public string? IdCardPhotoUrl { get; set; }

    [MaxLength(100)]
    public string? Email { get; set; }                      // 邮箱

    [MaxLength(500)]
    public string? AvatarUrl { get; set; }                  // 头像图片地址

    /// <summary>钉钉用户 userid（从钉钉同步打卡时用来对应本地员工，没对接则为空）</summary>
    [MaxLength(64)]
    public string? DingTalkUserId { get; set; }

    public DateOnly? HireDate { get; set; }                 // 入职日期

    public bool IsActive { get; set; } = true;              // 是否在职（false=已停用，不能登录）

    /// <summary>是否在黑名单（永不录用）。拉黑时同时把 IsActive 置 false，禁止登录。</summary>
    public bool IsBlacklisted { get; set; } = false;

    /// <summary>员工状态（由 IsActive/IsBlacklisted 推导，仅展示/筛选用，不单独存库）：在职 / 已停用 / 黑名单。</summary>
    [NotMapped]
    public EmployeeStatus Status => IsBlacklisted ? EmployeeStatus.Blacklisted
                                  : IsActive      ? EmployeeStatus.Active
                                  :                 EmployeeStatus.Disabled;

    public DateTime? LastLoginAt { get; set; }              // 最后一次登录时间

    public DateTime CreatedAt { get; set; } = DateTime.Now; // 这条记录的创建时间
    public DateTime UpdatedAt { get; set; } = DateTime.Now; // 这条记录的最后修改时间

    // ── 导航属性：指向这条员工记录关联到的其它表 ──────────────────────────
    [ForeignKey("DepartmentId")]
    public Department? Department { get; set; }             // 他所在的部门

    [ForeignKey("AttendanceGroupId")]
    public AttendanceGroup? AttendanceGroup { get; set; }   // 他所在的考勤组

    [ForeignKey("SupervisorUserId")]
    public User? Supervisor { get; set; }                   // 他的直属上级（也是一个 User）

    public ICollection<AttendanceRecord>  AttendanceRecords  { get; set; } = [];  // 他的所有考勤日记录
    public ICollection<ApprovalRequest>   ApprovalRequests   { get; set; } = [];  // 他提交的所有审批申请
    public ICollection<ShiftAssignment>   ShiftAssignments   { get; set; } = [];  // 他的所有排班
    public ICollection<Notification>      Notifications      { get; set; } = [];  // 他收到的所有通知
}
