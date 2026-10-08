using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Approval;

/// <summary>
/// 总审批记录：把员工提交的全部申请（补卡/请假/加班/出差）连同各级审批结果集中在一页看，并能导出 Excel。
/// 所有管理员/文员账号都能打开（ManagePolicy），数据按各自的管理范围收窄——分公司管理员/文员只看到自己范围内
/// 员工的申请，总部管理员看全部；范围规则和"待我审批""月度报表"用的是同一套（DeptScopeService）。
/// 只保留最近 <see cref="WindowMonths"/> 个月内提交的记录（更早的不在这里显示、也导不出来）。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class ApprovalRecordsModel(
    IApprovalService approvalService, IDeptScopeService deptScopeService,
    AttendanceDbContext db, ILogger<ApprovalRecordsModel> logger) : PageModel
{
    /// <summary>只保留最近几个月的记录。</summary>
    public const int WindowMonths = 2;
    public const int PageSize = 20;
    /// <summary>一次导出最多多少条（防止一次导出把内存撑爆；2 个月的数据远达不到这个量级）。</summary>
    public const int MaxExportRows = 20000;

    // ── 当前筛选条件（页面回显用）──
    public DateOnly StartDate  { get; set; }
    public DateOnly EndDate    { get; set; }
    public string?  Type       { get; set; }
    public string?  Status     { get; set; }
    public int?     DeptId     { get; set; }
    public string?  Keyword    { get; set; }
    public int      PageIndex  { get; set; } = 1;

    public DateOnly WindowStart => GetWindowStart(DateOnly.FromDateTime(DateTime.Today));

    public List<ApprovalRequestDto> Items { get; set; } = [];
    public int Total { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    /// <summary>部门下拉里要不要有"全部部门"：只有不受限的总部账号才有；分公司账号只能在自己范围内选。</summary>
    public bool ShowAllDepts { get; set; }

    public record DeptOption(int Id, string Name, int Depth);
    public List<DeptOption> DeptOptions { get; set; } = [];

    /// <summary>"最近 N 个月"的起点：今天往前推 N 个月。</summary>
    public static DateOnly GetWindowStart(DateOnly today) => today.AddMonths(-WindowMonths);

    /// <summary>
    /// 把用户传来的日期范围收进"最近 N 个月、不晚于今天"里：没传起点就从窗口起点开始，比窗口起点还早的按窗口起点算
    /// （地址栏里手改日期也翻不出更早的数据）；没传终点/终点在未来就按今天算；终点早于起点时按起点算。
    /// </summary>
    public static (DateOnly Start, DateOnly End) ClampRange(DateOnly? start, DateOnly? end, DateOnly today)
    {
        var windowStart = GetWindowStart(today);
        var s = start is null || start.Value < windowStart ? windowStart : start.Value;
        if (s > today) s = today;
        var e = end is null || end.Value > today ? today : end.Value;
        if (e < s) e = s;
        return (s, e);
    }

    public async Task OnGetAsync(DateOnly? start, DateOnly? end, string? type, string? status, int? deptId, string? keyword, int p = 1)
    {
        var cu = HttpContext.GetRequiredUser();
        var (query, deptIds, effDept) = await BuildQueryAsync(cu, start, end, type, status, deptId, keyword);
        query.PageIndex = Math.Max(1, p);
        query.PageSize  = PageSize;

        (Items, Total) = await approvalService.QueryApprovalsAsync(query, deptIds);
        PageIndex = query.PageIndex;
        DeptId    = effDept;
        ShowAllDepts = !cu.IsScoped;
        await LoadDeptOptionsAsync(cu);
    }

    /// <summary>按当前筛选条件，把 2 个月内匹配到的全部申请导出成 Excel（不分页，最多 <see cref="MaxExportRows"/> 条）。</summary>
    public async Task<IActionResult> OnGetExportAsync(DateOnly? start, DateOnly? end, string? type, string? status, int? deptId, string? keyword)
    {
        var cu = HttpContext.GetRequiredUser();
        var (query, deptIds, _) = await BuildQueryAsync(cu, start, end, type, status, deptId, keyword);
        query.PageIndex = 1;
        query.PageSize  = MaxExportRows;

        var (items, total) = await approvalService.QueryApprovalsAsync(query, deptIds);
        var bytes = ExcelExportHelper.ExportApprovalRecords(items, StartDate, EndDate);

        // 导出内容含员工的请假理由/出差目的地等，留一条审计日志（谁在什么时候导出了多少条）
        logger.LogInformation("管理员 {OperatorNo} 导出了总审批记录 Excel，共 {Count} 条（提交时间 {Start} 至 {End}，类型={Type}，状态={Status}，部门={DeptId}，关键字={Keyword}）",
            User.FindFirstValue(ClaimTypes.Name) ?? "?", items.Count, StartDate, EndDate, type, status, deptId, keyword);
        if (total > items.Count)
            logger.LogWarning("总审批记录导出超过上限 {Max} 条，实际匹配 {Total} 条，只导出了前 {Count} 条", MaxExportRows, total, items.Count);

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"总审批记录_{StartDate:yyyyMMdd}-{EndDate:yyyyMMdd}.xlsx");
    }

    /// <summary>把页面传来的筛选条件整理成查询条件：日期收进 2 个月窗口、部门收进当前账号的管理范围。</summary>
    private async Task<(ApprovalQueryDto Query, HashSet<int>? DeptIds, int? EffectiveDeptId)> BuildQueryAsync(
        CurrentUser cu, DateOnly? start, DateOnly? end, string? type, string? status, int? deptId, string? keyword)
    {
        var (s, e) = ClampRange(start, end, DateOnly.FromDateTime(DateTime.Today));
        StartDate = s; EndDate = e;
        Type = type; Status = status; Keyword = keyword;

        // 部门：不受限用户原样用传入值（null=全部）；受限用户（分公司管理员/文员）只能在自己范围内，
        // 没选或选到范围外的一律收窄成自己的管理范围。选中的部门含所有下级部门。
        var effDept = await deptScopeService.ResolveEffectiveDeptIdAsync(cu, deptId);
        HashSet<int>? deptIds = effDept.HasValue ? await deptScopeService.GetSubtreeIdsAsync(effDept.Value) : null;

        var query = new ApprovalQueryDto
        {
            StartDate      = s.ToDateTime(TimeOnly.MinValue),
            EndDate        = e.ToDateTime(new TimeOnly(23, 59, 59)),
            Keyword        = keyword,
            ApprovalType   = Enum.TryParse<ApprovalType>(type, out var t) && Enum.IsDefined(t) ? t : null,
            ApprovalStatus = Enum.TryParse<ApprovalStatus>(status, out var st) && Enum.IsDefined(st) ? st : null
        };
        return (query, deptIds, effDept);
    }

    /// <summary>部门下拉选项：按"父部门在前、子部门缩进"摊平；受限管理员只看到自己范围内的部门。</summary>
    private async Task LoadDeptOptionsAsync(CurrentUser cu)
    {
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
        var depts = await db.Departments.Where(d => d.IsActive)
            .OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
        if (visibleIds is not null)
            depts = depts.Where(d => visibleIds.Contains(d.Id)).ToList();
        var byParent = depts.GroupBy(d => d.ParentId ?? 0).ToDictionary(g => g.Key, g => g.ToList());

        DeptOptions = [];
        void Walk(int parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var kids)) return;
            foreach (var d in kids)
            {
                DeptOptions.Add(new DeptOption(d.Id, d.DeptName, depth));
                Walk(d.Id, depth + 1);
            }
        }
        if (cu.IsScoped)
        {
            var root = depts.FirstOrDefault(d => d.Id == cu.ScopedDepartmentId!.Value);
            if (root is not null) { DeptOptions.Add(new DeptOption(root.Id, root.DeptName, 0)); Walk(root.Id, 1); }
        }
        else
        {
            Walk(0, 0);
        }
    }
}
