using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Notice;

/// <summary>
/// 发布公告：管理员/文员范围随便选（全公司/按部门/按考勤组）；
/// 班组长/主管范围锁死为"我的直属下属"，页面上不给选择器，只提示会发给多少人。
/// 分公司管理员（受部门范围限定）不能选"全公司"，"按部门/按考勤组"也只能选自己范围内的。
/// </summary>
[Authorize(Policy = "ApprovePolicy")]
public class PublishModel(IAnnouncementService announcementService, IDeptScopeService deptScopeService, AttendanceDbContext db) : AppPageModel
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
            var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetCurrentUser()!);
            var depts  = await announcementService.GetDepartmentOptionsAsync();
            var groups = await announcementService.GetAttendanceGroupOptionsAsync();
            if (visibleIds is null)
            {
                DeptOptions  = depts;
                GroupOptions = groups;
            }
            else
            {
                // 受限管理员：部门候选按自己范围过滤；考勤组候选按"关联部门在自己范围内（或完全没关联，
                // 当全公司通用组）"过滤——口径跟考勤组管理页保持一致
                DeptOptions = depts.Where(d => visibleIds.Contains(d.Id)).ToList();
                var groupDeptMap = (await db.Departments.Where(d => d.AttendanceGroupId != null)
                        .Select(d => new { d.Id, d.AttendanceGroupId }).ToListAsync())
                    .GroupBy(x => x.AttendanceGroupId!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
                GroupOptions = groups.Where(g => !groupDeptMap.TryGetValue(g.Id, out var deptIds)
                    || deptIds.Any(visibleIds.Contains)).ToList();
            }
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

            // 服务端重新校验一遍范围，不能只信前端下拉框只显示了范围内的选项——受限管理员不能选
            // "全公司"，"按部门/按考勤组"选的那个 id 也必须真的落在自己范围内
            if (IsManager)
            {
                var cu = HttpContext.GetCurrentUser()!;
                if (cu.IsScoped)
                {
                    if (scopeType == AnnouncementScopeType.All)
                        throw new InvalidOperationException("无权发布全公司范围的公告");
                    if (scopeType == AnnouncementScopeType.Department)
                    {
                        if (!await deptScopeService.CanAccessDeptAsync(cu, ScopeId))
                            throw new InvalidOperationException("无权向该部门发布公告");
                    }
                    else if (scopeType == AnnouncementScopeType.AttendanceGroup && ScopeId.HasValue)
                    {
                        var groupDeptIds = await db.Departments.Where(d => d.AttendanceGroupId == ScopeId.Value)
                            .Select(d => d.Id).ToListAsync();
                        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
                        var allowed = groupDeptIds.Count == 0 || groupDeptIds.Any(id => visibleIds!.Contains(id));
                        if (!allowed) throw new InvalidOperationException("无权向该考勤组发布公告");
                    }
                }
            }

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
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetCurrentUser()!);
        var ok = await announcementService.WithdrawAsync(CurrentUserId, IsManager, id, visibleIds);
        if (ok) SuccessMessage = "已撤下该公告";
        else    ErrorMessage   = "操作失败，请重试";

        await LoadAsync();
        return Page();
    }

    /// <summary>"查看已读详情"弹窗：AJAX 拉某条公告的已读明细（谁读了谁没读）。</summary>
    public async Task<JsonResult> OnGetReadDetailAsync(int id)
    {
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetCurrentUser()!);
        var detail = await announcementService.GetReadDetailAsync(CurrentUserId, IsManager, id, visibleIds);
        return new JsonResult(detail ?? new List<AnnouncementReadDetailDto>());
    }
}
