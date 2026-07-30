using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Notice;

/// <summary>
/// 发布公告：管理员/文员范围随便选（全公司/按部门/按考勤组）；
/// 班组长/主管范围锁死为"我的直属下属"，页面上不给选择器，只提示会发给多少人。
/// </summary>
[Authorize(Policy = "ApprovePolicy")]
public class PublishModel(IAnnouncementService announcementService) : AppPageModel
{
    public List<AnnouncementPublishedItemDto> MyPublished  { get; set; } = [];
    public List<AnnouncementScopeOptionDto>   DeptOptions  { get; set; } = [];
    public List<AnnouncementScopeOptionDto>   GroupOptions { get; set; } = [];
    public int  DirectReportCount { get; set; }

    /// <summary>是不是"管理员/文员"——算出来的，不依赖 LoadAsync 有没有跑过，各个 OnPost 里可以直接用。</summary>
    public bool IsManager => CurrentRole is UserRole.Admin or UserRole.Clerk;

    [TempData] public string? SuccessMessage { get; set; }
    [TempData] public string? ErrorMessage   { get; set; }

    [BindProperty] public string Title     { get; set; } = string.Empty;
    [BindProperty] public string Body      { get; set; } = string.Empty;   // 公告正文（不叫 Content，避免和 PageModel.Content() 这个继承方法同名）
    [BindProperty] public string ScopeType { get; set; } = nameof(AttendanceSystem.Models.Enums.AnnouncementScopeType.All);
    [BindProperty] public int?  ScopeId    { get; set; }

    private UserRole CurrentRole => HttpContext.GetCurrentUser()?.Role ?? UserRole.Employee;

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        MyPublished = await announcementService.GetMyPublishedAsync(CurrentUserId);
        if (IsManager)
        {
            DeptOptions  = await announcementService.GetDepartmentOptionsAsync();
            GroupOptions = await announcementService.GetAttendanceGroupOptionsAsync();
        }
        else
        {
            DirectReportCount = await announcementService.CountDirectReportsAsync(CurrentUserId);
        }
    }

    /// <summary>点"发布"时执行。</summary>
    public async Task<IActionResult> OnPostPublishAsync()
    {
        try
        {
            if (!Enum.TryParse<AnnouncementScopeType>(ScopeType, out var scopeType))
                scopeType = AnnouncementScopeType.All;

            await announcementService.PublishAsync(CurrentUserId, CurrentRole, new PublishAnnouncementDto
            {
                Title     = Title,
                Content   = Body,
                ScopeType = scopeType,
                ScopeId   = ScopeId
            });
            SuccessMessage = "公告已发布";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        await LoadAsync();
        return Page();
    }

    /// <summary>点"撤下"时执行（软删除，历史记录和已读数据都保留）。</summary>
    public async Task<IActionResult> OnPostWithdrawAsync(int id)
    {
        var ok = await announcementService.WithdrawAsync(CurrentUserId, IsManager, id);
        if (ok) SuccessMessage = "已撤下该公告";
        else    ErrorMessage   = "操作失败，请重试";

        await LoadAsync();
        return Page();
    }

    /// <summary>"查看已读详情"弹窗：AJAX 拉某条公告的已读明细（谁读了谁没读）。</summary>
    public async Task<JsonResult> OnGetReadDetailAsync(int id)
    {
        var detail = await announcementService.GetReadDetailAsync(CurrentUserId, IsManager, id);
        return new JsonResult(detail ?? new List<AnnouncementReadDetailDto>());
    }
}
