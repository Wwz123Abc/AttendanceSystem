using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Attendance;

/// <summary>
/// 员工"考勤核对"页（2026-11-03 新增）：列出近 7 天的考勤记录，员工自己核对；
/// 对哪一天有疑问点「有异议」，填写原因提交；不操作就视为认可系统记录。
/// 提交后管理员/文员会收到通知并处理，处理结果会回在这里并通知员工。
/// </summary>
[Authorize]
public class VerifyModel(IAttendanceDisputeService disputeService) : AppPageModel
{
    [TempData] public string? SuccessMessage { get; set; }
    [TempData] public string? ErrorMessage   { get; set; }

    public List<AttendanceVerifyRowDto> Rows { get; set; } = [];
    public int VerifyDays => disputeService.VerifyDays;

    /// <summary>可以在页面上选的日期（近 7 天里"有记录或有排班"的那些天）。</summary>
    public List<AttendanceVerifyRowDto> SelectableDays => Rows.Where(r => r.DisputeStatus is null or AttendanceDisputeStatus.Rejected or AttendanceDisputeStatus.Cancelled).ToList();

    public async Task OnGetAsync(CancellationToken ct) => Rows = await disputeService.GetVerifyListAsync(CurrentUserId, ct);

    public async Task<IActionResult> OnPostSubmitAsync(DateOnly workDate, int type, string? reason, CancellationToken ct)
    {
        try
        {
            if (!Enum.IsDefined(typeof(AttendanceDisputeType), type))
                throw new BusinessException("请选择问题类型");
            await disputeService.SubmitAsync(CurrentUserId, workDate, (AttendanceDisputeType)type, reason ?? "", null, ct);
            SuccessMessage = $"{workDate:MM-dd} 的异议已提交，管理员/文员会尽快核对处理，处理结果会通知你";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) { ErrorMessage = "提交失败，请稍后重试"; System.Diagnostics.Debug.WriteLine(ex); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelAsync(int id, CancellationToken ct)
    {
        try
        {
            await disputeService.CancelAsync(CurrentUserId, id, ct);
            SuccessMessage = "已撤回该异议";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        return RedirectToPage();
    }
}
