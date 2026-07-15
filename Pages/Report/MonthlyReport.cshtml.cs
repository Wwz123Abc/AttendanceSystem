using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Report;

/// <summary>
/// 月度报表页：页面上直接按"导出模板汇总表"同一套列结构（含每日打卡格子）展示考勤数据，
/// 可以自选统计的起止日期、按公司/部门筛选范围、按姓名或工号搜索，并能导出模板格式 Excel/个人明细。需管理员/文员。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class MonthlyReportModel(IAttendanceService attendanceService, AttendanceDbContext db) : PageModel
{
    // Excel 文件的类型标识
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>页面表格每页显示的人数（列很多，一页显示太多人会很卡，所以要分页）。</summary>
    public const int PageSize = 30;

    /// <summary>本次统计的起止日期（默认"上月26号至本月25号"，管理员可以在页面上改成任意起止日期）。</summary>
    public DateOnly Start { get; set; }
    public DateOnly End   { get; set; }

    /// <summary>本次筛选勾选的部门编号（空=不筛选，全公司）。</summary>
    public List<int> SelectedDeptIds { get; set; } = [];

    /// <summary>按姓名或工号搜索的关键字（空=不搜索）。</summary>
    public string? Keyword { get; set; }

    public int PageIndex    { get; set; } = 1;
    public int TotalMatched { get; set; }

    /// <summary>统计周期内每一天，按顺序排列（表格里"考勤结果"那一组列的表头，和每行的 Rows[].DailyHours 下标一一对应）。</summary>
    public List<DateOnly> Dates { get; set; } = [];

    /// <summary>当前页要显示的员工数据行（已按关键字筛选、分页截取）。</summary>
    public List<TemplateReportRowDto> Rows { get; set; } = [];

    /// <summary>"导出个人明细"用：取统计周期结束日期所在的年/月（明细导出仍按自然月）。</summary>
    public int Year  { get; set; }
    public int Month { get; set; }

    /// <summary>
    /// "模板月度汇总表"用的"公司/部门"合并树：第 0 层是公司（虚拟节点，不对应具体部门），
    /// 下面挂着按公司分组的真实部门树。勾选公司节点会连带勾上它下面所有部门（范围大）；
    /// 也可以只勾某个具体部门（范围小）。RealDeptId 为空的是公司虚拟节点，不会被当作筛选条件提交。
    /// </summary>
    public record CompanyDeptNode(string NodeId, string? ParentNodeId, string Name, int Depth, int? RealDeptId);
    public List<CompanyDeptNode> DeptTree { get; set; } = [];

    /// <summary>打开页面：按筛选条件（起止日期/部门/关键字）查出要展示的数据，分页截取当前页。</summary>
    public async Task OnGetAsync(DateOnly? start, DateOnly? end, List<int>? deptIds, string? keyword, int p = 1)
    {
        var today      = DateOnly.FromDateTime(DateTime.Today);
        var defaultEnd = new DateOnly(today.Year, today.Month, 25);

        End   = end   ?? defaultEnd;
        Start = start ?? End.AddMonths(-1).AddDays(1);
        if (End < Start) (Start, End) = (End, Start);            // 万一日期选反了，自动交换纠正，不直接报错卡住整页
        if (End.DayNumber - Start.DayNumber > 366) End = Start.AddDays(366);  // 防止选了个离谱的超长区间，撑爆页面

        SelectedDeptIds = deptIds ?? [];
        Keyword         = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        PageIndex       = p < 1 ? 1 : p;

        Year  = End.Year;
        Month = End.Month;

        var result = await attendanceService.GenerateTemplateReportAsync(
            Start, End, SelectedDeptIds.Count > 0 ? SelectedDeptIds : null);
        Dates = result.Dates;

        var filtered = Keyword is null
            ? result.Rows
            : result.Rows.Where(r => (r.RealName?.Contains(Keyword) ?? false) || (r.EmployeeNo?.Contains(Keyword) ?? false)).ToList();

        TotalMatched = filtered.Count;
        var totalPages = (int)Math.Ceiling(TotalMatched / (double)PageSize);
        if (totalPages > 0 && PageIndex > totalPages) PageIndex = totalPages;

        Rows = filtered.Skip((PageIndex - 1) * PageSize).Take(PageSize).ToList();

        await LoadDeptTreeAsync();
    }

    /// <summary>
    /// 组装"公司/部门"合并树：部门自己没填"所属公司"时，顺着上级部门一路往上找，
    /// 找到最近的一个非空公司名就用它；一路到顶都没有就归到"未分类"这个桶里。
    /// </summary>
    private async Task LoadDeptTreeAsync()
    {
        var depts = await db.Departments.Where(d => d.IsActive)
            .OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
        var byId     = depts.ToDictionary(d => d.Id);
        var byParent = depts.Where(d => d.ParentId.HasValue)
            .GroupBy(d => d.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        string EffectiveCompany(Models.Entities.Department d)
        {
            var cur = d;
            for (var i = 0; i < 50 && cur is not null; i++)   // 50 只是防止脏数据出现循环引用导致死循环，正常部门树远用不到这么深
            {
                if (!string.IsNullOrWhiteSpace(cur.CompanyName)) return cur.CompanyName;
                cur = cur.ParentId.HasValue && byId.TryGetValue(cur.ParentId.Value, out var p) ? p : null;
            }
            return "未分类";
        }

        DeptTree = [];
        var rootDepts = depts.Where(d => !d.ParentId.HasValue || !byId.ContainsKey(d.ParentId.Value)).ToList();
        var companies = rootDepts.GroupBy(EffectiveCompany).OrderBy(g => g.Key).ToList();

        void AddNode(Models.Entities.Department d, string parentNodeId, int depth)
        {
            var nodeId = $"d{d.Id}";
            DeptTree.Add(new CompanyDeptNode(nodeId, parentNodeId, d.DeptName, depth, d.Id));
            if (byParent.TryGetValue(d.Id, out var kids))
                foreach (var k in kids) AddNode(k, nodeId, depth + 1);
        }

        var coIndex = 0;
        foreach (var co in companies)
        {
            var coNodeId = $"co{coIndex++}";
            DeptTree.Add(new CompanyDeptNode(coNodeId, null, co.Key, 0, null));
            foreach (var root in co) AddNode(root, coNodeId, 1);
        }
    }

    /// <summary>
    /// 点“导出模板汇总表”时执行：统计周期由管理员在页面上自选起止日期决定，
    /// deptIds 来自页面的公司/部门合并树勾选结果（和页面上表格看到的范围一致，不受关键字搜索影响——
    /// 导出是给公司存档/发工资用的完整表，关键字搜索只是页面上方便肉眼找人用的，不应该影响导出范围）。
    /// </summary>
    public async Task<IActionResult> OnGetExportTemplateAsync(DateOnly? start, DateOnly? end, List<int>? deptIds)
    {
        if (start is null || end is null) return BadRequest("请选择起止日期");
        if (end < start) return BadRequest("结束日期不能早于开始日期");
        if (end.Value.DayNumber - start.Value.DayNumber > 366) return BadRequest("统计周期不能超过 366 天");

        var result = await attendanceService.GenerateTemplateReportAsync(start.Value, end.Value, deptIds);
        var bytes  = ExcelExportHelper.ExportTemplateReport(result);
        return File(bytes, XlsxContentType, $"月度汇总_{start:yyyyMMdd}-{end:yyyyMMdd}.xlsx");
    }

    /// <summary>
    /// 点"导出打卡时间表"时执行：范围和"导出模板汇总表"一样，按当前起止日期/部门勾选（不受关键字搜索影响），
    /// 但内容换成一行一人一天的打卡明细（上下班时间、工时、迟到等），方便核对具体打卡时间用。
    /// </summary>
    public async Task<IActionResult> OnGetExportClockTimeSheetAsync(DateOnly? start, DateOnly? end, List<int>? deptIds)
    {
        if (start is null || end is null) return BadRequest("请选择起止日期");
        if (end < start) return BadRequest("结束日期不能早于开始日期");
        if (end.Value.DayNumber - start.Value.DayNumber > 366) return BadRequest("统计周期不能超过 366 天");

        var records = await attendanceService.GetClockTimeSheetAsync(start.Value, end.Value, deptIds);
        var bytes   = ExcelExportHelper.ExportClockTimeSheet(records, start.Value, end.Value);
        return File(bytes, XlsxContentType, $"打卡时间表_{start:yyyyMMdd}-{end:yyyyMMdd}.xlsx");
    }

    /// <summary>点某人“导出明细”时执行。</summary>
    public async Task<IActionResult> OnGetExportDetailAsync(int userId, int year, int month)
    {
        var rangeError = ValidateYearMonth(year, month);
        if (rangeError != null) return BadRequest(rangeError);

        await attendanceService.GenerateMonthlySummaryAsync(year, month);
        var all    = await attendanceService.GetDeptMonthlySummariesAsync(null, null, year, month);
        var target = all.FirstOrDefault(s => s.UserId == userId);   // 找这个人的汇总
        if (target is null) return NotFound();

        // 补上这个人的每日明细（汇总列表里默认不带明细）
        var records = await attendanceService.GetPersonalAttendanceAsync(new PersonalAttendanceQueryDto
        {
            UserId = userId, Year = year, Month = month
        });
        target.DailyRecords = records;

        var bytes = ExcelExportHelper.ExportDailyStatusReport(target);
        return File(bytes, XlsxContentType, $"{target.RealName}_{year}年{month:D2}月考勤明细.xlsx");
    }

    /// <summary>
    /// 校验导出用的年/月是否合理：正常情况下都是从"导出明细"按钮带过来的，不会有问题；
    /// 但导出走的是 GET 请求，网址上的 year/month 是可以被人手改的，改成 month=13 这种值
    /// 会导致后面构造 DateOnly 时直接崩溃（报服务器 500 错误），所以要提前挡住。
    /// </summary>
    private static string? ValidateYearMonth(int year, int month)
    {
        if (month is < 1 or > 12) return "月份不正确";
        if (year is < 2000 or > 2100) return "年份不正确";
        return null;
    }
}
