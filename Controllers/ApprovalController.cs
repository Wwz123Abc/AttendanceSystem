using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Controllers;

/// <summary>审批接口：提交申请、审批处理、撤销、查询。需登录。</summary>
[Authorize]
[Route("api/[controller]")]
[ApiController]
public class ApprovalController(IApprovalService approvalService) : ApiControllerBase
{
    /// <summary>员工提交审批申请。</summary>
    [HttpPost("submit")]
    public async Task<IActionResult> Submit([FromBody] SubmitApprovalDto dto)
    {
        var request = await approvalService.SubmitApprovalAsync(CurrentUserId, dto);
        return Ok(new { Success = true, Message = "申请已提交", RequestNo = request.RequestNo });
    }

    /// <summary>审批人处理（通过 / 驳回），需审批权限。</summary>
    [HttpPost("handle")]
    [Authorize(Policy = "ApprovePolicy")]
    public async Task<IActionResult> Handle([FromBody] HandleApprovalDto dto)
    {
        if (!dto.IsApproved && string.IsNullOrWhiteSpace(dto.Comment))   // 驳回必须写原因
            return BadRequest(new { Success = false, Message = "驳回时必须填写驳回原因" });
        var ok = await approvalService.HandleApprovalAsync(CurrentUserId, dto);
        return Ok(new { Success = ok, Message = ok ? "处理成功" : "未找到待处理的审批记录" });
    }

    /// <summary>员工撤销自己的申请。</summary>
    [HttpPost("cancel/{id:int}")]
    public async Task<IActionResult> Cancel(int id)
    {
        var ok = await approvalService.CancelApprovalAsync(CurrentUserId, id);
        return Ok(new { Success = ok, Message = ok ? "已撤销" : "仅待审批状态可撤销" });
    }

    /// <summary>我提交的申请列表。</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> GetMine([FromQuery] ApprovalStatus? status)
    {
        var list = await approvalService.GetMyApprovalsAsync(CurrentUserId, status);
        return Ok(new { Success = true, Data = list, Total = list.Count });
    }

    /// <summary>待我审批列表，需审批权限。</summary>
    [HttpGet("pending-for-me")]
    [Authorize(Policy = "ApprovePolicy")]
    public async Task<IActionResult> GetPendingForMe()
    {
        var list = await approvalService.GetPendingForApproverAsync(CurrentUserId);
        return Ok(new { Success = true, Data = list, Total = list.Count });
    }

    /// <summary>申请详情（仅本人 / 该单审批人 / 管理员文员可见）。</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetDetail(int id)
    {
        // 判断当前用户是不是管理员或文员
        bool isManager = User.IsInRole(nameof(UserRole.Admin)) || User.IsInRole(nameof(UserRole.Clerk));
        var detail = await approvalService.GetApprovalDetailAsync(id, CurrentUserId, isManager);
        return detail is null
            ? NotFound(new { Success = false, Message = "申请不存在或无权查看" })
            : Ok(new { Success = true, Data = detail });
    }

    /// <summary>审批记录分页查询（管理员查全部）。</summary>
    [HttpGet("query")]
    [Authorize(Policy = "ManagePolicy")]
    public async Task<IActionResult> Query([FromQuery] ApprovalQueryDto query)
    {
        var (items, total) = await approvalService.QueryApprovalsAsync(query);
        return Ok(new { Success = true, Data = items, Total = total });
    }
}
