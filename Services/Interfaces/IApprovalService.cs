using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>审批业务服务契约（补卡 / 请假 / 加班）。</summary>
public interface IApprovalService
{
    /// <summary>提交审批申请，按审批流生成审批节点并通知首个审批人。</summary>
    Task<ApprovalRequest> SubmitApprovalAsync(int applicantUserId, SubmitApprovalDto dto);
    /// <summary>审批人处理（通过则流转下一节点或终审，驳回则整单驳回）。</summary>
    Task<bool> HandleApprovalAsync(int approverUserId, HandleApprovalDto dto);
    /// <summary>申请人撤销申请（仅「待审批」状态可撤销）。</summary>
    Task<bool> CancelApprovalAsync(int userId, int approvalRequestId);
    /// <summary>审批记录分页查询。</summary>
    Task<(List<ApprovalRequestDto> Items, int Total)> QueryApprovalsAsync(ApprovalQueryDto query);
    /// <summary>
    /// 审批申请详情（含各审批节点）。仅「申请人本人 / 该单审批人 / 管理员(文员)」可查看，
    /// 其他人返回 null，避免越权查看他人申请的事由与附件。
    /// </summary>
    Task<ApprovalRequestDto?> GetApprovalDetailAsync(int id, int requesterUserId, bool isManager);
    /// <summary>查询待某审批人处理的申请列表。</summary>
    Task<List<ApprovalRequestDto>> GetPendingForApproverAsync(int approverUserId);
    /// <summary>查询某用户提交的申请列表（可按状态过滤）。</summary>
    Task<List<ApprovalRequestDto>> GetMyApprovalsAsync(int userId, ApprovalStatus? status = null);
    /// <summary>生成审批单号（前缀 + 日期 + 当日流水号）。</summary>
    Task<string> GenerateRequestNoAsync(ApprovalType type);
}
