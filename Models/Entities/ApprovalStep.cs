using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

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
