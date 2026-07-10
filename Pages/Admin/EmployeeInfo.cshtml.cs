using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 员工信息页：把系统里已知的某个员工的全部资料——基本信息、组织信息、联系方式、证件、
/// 紧急联系人、合同/入职信息、账号状态——整合到一页展示，方便管理员一次看全、也方便打印存档。
/// 只读页面，不提供编辑（编辑仍在"员工管理"页完成，避免同一份资料维护两套表单）。
/// 支持按部门筛选/按姓名工号搜索来缩小"切换员工"名单，并能把筛选后的结果批量导出成 Excel。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class EmployeeInfoModel(IUserService userService, AttendanceDbContext db) : PageModel
{
    public User?   Employee { get; set; }
    public string? Keyword  { get; set; }
    public int?    DeptId   { get; set; }

    /// <summary>左侧"切换员工"用的候选名单：按部门/关键字过滤，最多取 20 条，避免几千号人一次性全部列出来。</summary>
    public List<User> QuickList { get; set; } = [];

    /// <summary>命中当前筛选条件的总人数（导出按钮上显示，让管理员知道点了会导出多少条）。</summary>
    public int TotalMatched { get; set; }

    /// <summary>部门下拉选项：按层级缩进展示，选中后含下级部门（和"员工管理"页部门筛选口径一致）。</summary>
    public record DeptOption(int Id, string Name, int Depth);
    public List<DeptOption> DeptOptions { get; set; } = [];

    public async Task OnGetAsync(int? id, string? keyword, int? deptId)
    {
        Keyword = keyword;
        DeptId  = deptId;

        // "切换员工"候选名单：复用员工管理页同一套查询（部门筛选含下级、关键字匹配姓名或工号），
        // 保证这里筛出来的人和"员工管理"页勾同样条件时看到的是同一批人。
        var (list, total) = await userService.GetUsersAsync(deptId: deptId, keyword: keyword, pageIndex: 1, pageSize: 20);
        QuickList    = list;
        TotalMatched = total;

        await LoadDeptOptionsAsync();

        // 没指定要看谁，就默认看候选名单里的第一个（比如刚搜索/筛选出来的那批人）
        var targetId = id ?? QuickList.FirstOrDefault()?.Id;
        if (targetId is null) return;   // 系统里还没有员工，或者筛选条件没搜到人

        Employee = await userService.GetUserWithDetailsAsync(targetId.Value);
    }

    /// <summary>按当前筛选条件（部门/关键字），把匹配到的全部员工基础资料导出成 Excel。</summary>
    public async Task<IActionResult> OnGetExportAsync(string? keyword, int? deptId)
    {
        var (users, _) = await userService.GetUsersAsync(deptId: deptId, keyword: keyword, pageIndex: 1, pageSize: 100_000);
        var bytes = ExcelExportHelper.ExportEmployeeList(users);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            System.Web.HttpUtility.UrlEncode($"员工信息_{DateTime.Now:yyyyMMdd}.xlsx"));
    }

    /// <summary>加载部门下拉选项：按"父部门在前、子部门缩进"的顺序摊平成一份列表。</summary>
    private async Task LoadDeptOptionsAsync()
    {
        var depts = await db.Departments.Where(d => d.IsActive)
            .OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
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
        Walk(0, 0);
    }
}
