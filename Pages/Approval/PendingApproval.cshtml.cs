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

    [BindProperty] public int    RequestId { get; set; }                // 要处理的申请单编号（单条通过/驳回用）
    [BindProperty] public string Comment   { get; set; } = string.Empty; // 审批意见
    [BindProperty] public List<int> BatchIds { get; set; } = [];         // 勾选要批量处理的申请单编号列表

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
        if (ValidateComment(required: false) is { } err)
            return await FailAsync(err);

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

    /// <summary>点“驳回”时执行。驳回必须填写意见，方便申请人知道被拒的原因。</summary>
    public async Task<IActionResult> OnPostRejectAsync()
    {
        if (ValidateComment(required: true) is { } err)
            return await FailAsync(err);

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

    /// <summary>批量“通过”：勾选列表里的每一条分别走一遍和单条通过完全一样的审批逻辑，互不影响，某条失败不会拦住其它条。</summary>
    public async Task<IActionResult> OnPostBatchApproveAsync()
    {
        if (ValidateComment(required: false) is { } err)
            return await FailAsync(err);
        return await HandleBatchAsync(approved: true);
    }

    /// <summary>批量“驳回”：审批意见必填，规则和单条驳回一致（一条驳回原因用于这一批全部申请）。</summary>
    public async Task<IActionResult> OnPostBatchRejectAsync()
    {
        if (ValidateComment(required: true) is { } err)
            return await FailAsync(err);
        return await HandleBatchAsync(approved: false);
    }

    /// <summary>批量通过/驳回的共同处理逻辑：逐条调用审批服务，最后汇总成功了几条。</summary>
    private async Task<IActionResult> HandleBatchAsync(bool approved)
    {
        var ids = BatchIds.Distinct().ToList();
        if (ids.Count == 0)
            return await FailAsync("请至少勾选一条申请");

        var okCount = 0;
        foreach (var id in ids)
        {
            var ok = await approvalService.HandleApprovalAsync(CurrentUserId, new HandleApprovalDto
            {
                ApprovalRequestId = id,
                IsApproved        = approved,
                Comment           = Comment
            });
            if (ok) okCount++;
        }

        Message   = $"批量{(approved ? "通过" : "驳回")}完成：成功 {okCount} / {ids.Count} 条";
        IsSuccess = okCount > 0;
        PendingItems = await approvalService.GetPendingForApproverAsync(CurrentUserId);
        return Page();
    }

    /// <summary>
    /// 校验审批意见：驳回（含批量驳回）必须填写原因；不管通过还是驳回，都不能超过 1000 字。
    /// 合法返回 null，不合法返回给用户看的错误提示。
    /// </summary>
    private string? ValidateComment(bool required)
    {
        // 意见是选填项：textarea 留空提交时，模型绑定会把空字符串转成 null（ASP.NET Core 默认行为），
        // 不能直接 Comment.Trim()，否则没填意见就点“通过”必然报空引用异常
        Comment ??= string.Empty;
        if (required && string.IsNullOrWhiteSpace(Comment)) return "驳回时请填写审批意见";
        if (Comment.Trim().Length > 1000) return "审批意见不能超过 1000 个字";
        return null;
    }

    /// <summary>校验不通过时的统一处理：把错误消息带回页面，并重新加载列表（否则页面上的表格会变空）。</summary>
    private async Task<IActionResult> FailAsync(string message)
    {
        Message   = message;
        IsSuccess = false;
        PendingItems = await approvalService.GetPendingForApproverAsync(CurrentUserId);
        return Page();
    }
}
