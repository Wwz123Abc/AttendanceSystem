using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 审批申请（对应数据库表 ApprovalRequest）：员工提交的一张补卡/请假/加班申请单。
/// 同一张表用不同字段装三类申请的信息（补卡用补卡字段，请假用请假字段……）。
/// </summary>
[Table("ApprovalRequest")]
public class ApprovalRequest
{
    [Key] public int Id { get; set; }                       // 主键

    /// <summary>申请单号（唯一，如 QJ202606250001）</summary>
    [Required, MaxLength(50)]
    public string RequestNo { get; set; } = string.Empty;

    public int ApplicantUserId { get; set; }                // 申请人（哪个员工提交的）

    public ApprovalType   ApprovalType   { get; set; }                          // 申请类型：补卡/请假/加班
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending; // 当前状态，默认“待审批”

    // ── 补卡字段（仅补卡申请用）────────────────────────────────────────────
    public DateOnly?  PunchDate { get; set; }               // 要补哪天的卡
    public PunchType? PunchType { get; set; }               // 补的是上班还是下班卡
    public TimeOnly?  PunchTime { get; set; }               // 补卡的时间点

    // ── 请假字段（仅请假申请用）────────────────────────────────────────────
    public LeaveType? LeaveType          { get; set; }      // 请假类型（事假/病假…）
    public DateTime?  LeaveStartTime     { get; set; }      // 请假开始
    public DateTime?  LeaveEndTime       { get; set; }      // 请假结束
    public decimal?   LeaveDurationHours { get; set; }      // 请假时长（小时）

    // ── 加班字段（仅加班申请用）────────────────────────────────────────────
    public DateTime? OvertimeStartTime     { get; set; }    // 加班开始
    public DateTime? OvertimeEndTime       { get; set; }    // 加班结束
    public decimal?  OvertimeDurationHours { get; set; }    // 加班时长（小时）

    // ── 出差字段（仅出差申请用）────────────────────────────────────────────
    public DateTime? BusinessTripStartTime   { get; set; }    // 出差开始
    public DateTime? BusinessTripEndTime     { get; set; }    // 出差结束
    public decimal?  BusinessTripDurationDays { get; set; }   // 出差天数
    [MaxLength(200)]
    public string?   BusinessTripDestination { get; set; }  // 出差目的地（可选）

    // ── 通用字段 ────────────────────────────────────────────────────────
    [MaxLength(1000)]
    public string? Reason { get; set; }                     // 申请理由

    /// <summary>附件地址列表（以 JSON 文本形式存多张图片/文件的地址）</summary>
    [MaxLength(2000)]
    public string? AttachmentUrls { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.Now;  // 提交时间
    public DateTime UpdatedAt   { get; set; } = DateTime.Now;  // 最后更新时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("ApplicantUserId")]
    public User Applicant { get; set; } = null!;                     // 申请人

    public ICollection<ApprovalStep> ApprovalSteps { get; set; } = []; // 这张单的各级审批节点
}

/// <summary>
/// 审批节点（对应数据库表 ApprovalStep）：记录每一级审批人是谁、审了没、什么意见。
/// 一张申请单可以有多级审批，对应多条节点。
/// </summary>
[Table("ApprovalStep")]
public class ApprovalStep
{
    [Key] public int Id { get; set; }                       // 主键

    public int ApprovalRequestId { get; set; }              // 属于哪张申请单
    public int ApproverUserId    { get; set; }              // 这一级的审批人是谁

    /// <summary>步骤序号（1 = 第一个审批人，按顺序往后审）</summary>
    public int StepOrder { get; set; } = 1;

    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;  // 这一级的处理结果

    /// <summary>审批意见（驳回时必填）</summary>
    [MaxLength(1000)]
    public string? Comment { get; set; }

    public DateTime? HandledAt { get; set; }                // 处理时间（还没处理则为空）
    public DateTime  CreatedAt { get; set; } = DateTime.Now; // 创建时间

    // ── 导航属性 ──────────────────────────────────────────────────────────
    [ForeignKey("ApprovalRequestId")]
    public ApprovalRequest ApprovalRequest { get; set; } = null!;   // 所属申请单

    [ForeignKey("ApproverUserId")]
    public User Approver { get; set; } = null!;                     // 这一级的审批人
}
