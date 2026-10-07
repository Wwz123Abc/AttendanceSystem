using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>审批节点展示 DTO（一条审批记录：谁审的、结果、意见）。</summary>
public class ApprovalStepDto
{
    public int            StepOrder      { get; set; }                  // 第几级
    public int            ApproverUserId { get; set; }                  // 审批人编号（用来判断"这一级是不是我"）
    public string         ApproverName   { get; set; } = string.Empty;  // 审批人姓名
    public ApprovalStatus Status       { get; set; }                  // 处理结果
    public string         StatusText   { get; set; } = string.Empty;  // 结果中文名
    public string?        Comment      { get; set; }                  // 审批意见
    public DateTime?      HandledAt    { get; set; }                  // 处理时间
    public string         HandledAtText => HandledAt?.ToString("yyyy-MM-dd HH:mm") ?? "待处理";  // 处理时间文字
}
