using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 管理端「考勤异议」页（2026-11-03 新增）：员工在"考勤核对"里提的异议集中在这里处理。
/// 权限：管理员 + 文员；分公司管理员/文员只能看到、处理自己部门范围内的员工的异议，
/// 且处理时服务层会再校验一次范围（防止直接改 id 越权处理别家公司的）。
/// 处理方式：先在「手动补卡/员工记录」里把考勤改对，再在这里写处理结果/理由并提交，员工会收到通知。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class DisputesModel(IAttendanceDisputeService disputeService, IDeptScopeService deptScopeService) : PageModel
{
    [TempData] public string? SuccessMessage { get; set; }
    [TempData] public string? ErrorMessage   { get; set; }

    [BindProperty(SupportsGet = true)] public int? Status    { get; set; }
    [BindProperty(SupportsGet = true)] public int  PageIndex { get; set; } = 1;

    public AttendanceDisputeListResultDto Result { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!AttendanceDisputeFeature.Enabled) return NotFound();   // 功能已下线（2026-11-03 用户要求）
        var cu = HttpContext.GetRequiredUser();
        var allowed = await deptScopeService.GetVisibleDeptIdsAsync(cu);
        var st = Status is { } s && Enum.IsDefined(typeof(AttendanceDisputeStatus), s) ? (AttendanceDisputeStatus?)s : null;
        Result = await disputeService.ListForHandlerAsync(
            new AttendanceDisputeQueryDto { Status = st, Page = PageIndex, PageSize = 50 }, allowed, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostHandleAsync(int id, bool accept, string? reply, CancellationToken ct)
    {
        if (!AttendanceDisputeFeature.Enabled) return NotFound();
        try
        {
            var cu = HttpContext.GetRequiredUser();
            var allowed = await deptScopeService.GetVisibleDeptIdsAsync(cu);
            await disputeService.HandleAsync(id, cu.UserId, cu.RealName, accept, reply ?? "", allowed, ct);
            SuccessMessage = accept ? "已处理，员工会收到通知" : "已驳回，员工会收到通知";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            ErrorMessage = "处理失败，请稍后重试";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return RedirectToPage(new { Status, PageIndex });
    }
}
