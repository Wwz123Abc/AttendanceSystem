namespace AttendanceSystem.Models.DTOs;

/// <summary>审批人处理申请时传的数据。</summary>
public class HandleApprovalDto
{
    public int    ApprovalRequestId { get; set; }   // 处理哪张申请单
    public bool   IsApproved        { get; set; }   // 通过(true) 还是 驳回(false)
    public string? Comment          { get; set; }   // 审批意见（驳回时必填）
}
