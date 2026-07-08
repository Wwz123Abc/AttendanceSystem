using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Report;

/// <summary>月度报表页：按月看部门考勤汇总，并导出汇总表/个人明细/模板格式 Excel。需管理员/文员。</summary>
[Authorize(Policy = "ManagePolicy")]
public class MonthlyReportModel(IAttendanceService attendanceService, AttendanceDbContext db) : PageModel
{
    // Excel 文件的类型标识
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public List<MonthlySummaryDto> Summaries { get; set; } = [];
    public int Year  { get; set; }
    public int Month { get; set; }

    /// <summary>
    /// "模板月度汇总表"导出用的"公司/部门"合并树：第 0 层是公司（虚拟节点，不对应具体部门），
    /// 下面挂着按公司分组的真实部门树。勾选公司节点会连带勾上它下面所有部门（范围大）；
    /// 也可以只勾某个具体部门（范围小）。RealDeptId 为空的是公司虚拟节点，不会被当作筛选条件提交。
    /// </summary>
    public record CompanyDeptNode(string NodeId, string? ParentNodeId, string Name, int Depth, int? RealDeptId);
    public List<CompanyDeptNode> DeptTree { get; set; } = [];

    /// <summary>打开页面：先按月生成最新汇总，再读出来显示。</summary>
    public async Task OnGetAsync(int? year, int? month)
    {
        Year  = year  ?? DateTime.Today.Year;
        Month = month ?? DateTime.Today.Month;

        await attendanceService.GenerateMonthlySummaryAsync(Year, Month);                    // 先算
        Summaries = await attendanceService.GetDeptMonthlySummariesAsync(null, null, Year, Month);  // 再取
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

    /// <summary>点“导出模板汇总表”时执行：统计周期是“上月26号至本月25号”，deptIds 来自页面的公司/部门合并树勾选结果。</summary>
    public async Task<IActionResult> OnGetExportTemplateAsync(int year, int month, List<int>? deptIds)
    {
        var end   = new DateOnly(year, month, 25);
        var start = end.AddMonths(-1).AddDays(1);
        var result = await attendanceService.GenerateTemplateReportAsync(start, end, deptIds);
        var bytes  = ExcelExportHelper.ExportTemplateReport(result);
        return File(bytes, XlsxContentType, $"月度汇总_{start:yyyyMMdd}-{end:yyyyMMdd}.xlsx");
    }

    /// <summary>点某人“导出明细”时执行。</summary>
    public async Task<IActionResult> OnGetExportDetailAsync(int userId, int year, int month)
    {
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
}
