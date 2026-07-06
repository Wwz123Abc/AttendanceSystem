using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Approval;

/// <summary>待我审批页：显示待办列表和详情，处理通过/驳回。需审批权限。</summary>
[Authorize(Policy = "ApprovePolicy")]
public class PendingApprovalModel(IApprovalService approvalService) : AppPageModel
{
    public List<ApprovalRequestDto> PendingItems  { get; set; } = [];   // 待我审批的列表
    public ApprovalRequestDto?      CurrentDetail { get; set; }         // 当前查看的某条详情
    public string? Message    { get; set; }
    public bool    IsSuccess  { get; set; }

    [BindProperty] public int    RequestId { get; set; }                // 要处理的申请单编号
    [BindProperty] public string Comment   { get; set; } = string.Empty; // 审批意见

    /// <summary>打开页面：加载待办列表；若带了 detailId，再加载那条详情。</summary>
    public async Task OnGetAsync(int? detailId)
    {
        PendingItems = await approvalService.GetPendingForApproverAsync(CurrentUserId);
        if (detailId.HasValue)
        {
            bool isManager = User.IsInRole(nameof(AttendanceSystem.Models.Enums.UserRole.Admin))
                          || User.IsInRole(nameof(AttendanceSystem.Models.Enums.UserRole.Clerk));
            CurrentDetail = await approvalService.GetApprovalDetailAsync(detailId.Value, CurrentUserId, isManager);
        }
    }

    /// <summary>点“通过”时执行。</summary>
    public async Task<IActionResult> OnPostApproveAsync()
    {
        var ok = await approvalService.HandleApprovalAsync(CurrentUserId, new HandleApprovalDto
        {
            ApprovalRequestId = RequestId,
            IsApproved        = true,
            Comment           = Comment
        });
        Message   = ok ? "已通过该申请" : "操作失败，请重试";
        IsSuccess = ok;
        PendingItems = await approvalService.GetPendingForApproverAsync(CurrentUserId);   // 刷新列表
        return Page();
    }

    /// <summary>点“驳回”时执行。</summary>
    public async Task<IActionResult> OnPostRejectAsync()
    {
        var ok = await approvalService.HandleApprovalAsync(CurrentUserId, new HandleApprovalDto
        {
            ApprovalRequestId = RequestId,
            IsApproved        = false,
            Comment           = Comment
        });
        Message   = ok ? "已驳回该申请" : "操作失败，请重试";
        IsSuccess = ok;
        PendingItems = await approvalService.GetPendingForApproverAsync(CurrentUserId);
        return Page();
    }
}
