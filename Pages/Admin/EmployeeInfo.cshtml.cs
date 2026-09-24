using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
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
public class EmployeeInfoModel(
    IUserService userService, IDeptScopeService deptScopeService,
    AttendanceDbContext db, ILogger<EmployeeInfoModel> logger) : PageModel
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
        var cu = HttpContext.GetCurrentUser()!;
        deptId = await deptScopeService.ResolveEffectiveDeptIdAsync(cu, deptId);

        Keyword = keyword;
        DeptId  = deptId;

        // "切换员工"候选名单：复用员工管理页同一套查询（部门筛选含下级、关键字匹配姓名或工号），
        // 保证这里筛出来的人和"员工管理"页勾同样条件时看到的是同一批人。
        var (list, total) = await userService.GetUsersAsync(deptId: deptId, keyword: keyword, pageIndex: 1, pageSize: 20);
        QuickList    = list;
        TotalMatched = total;

        await LoadDeptOptionsAsync(cu);

        // 没指定要看谁，就默认看候选名单里的第一个（比如刚搜索/筛选出来的那批人）
        var targetId = id ?? QuickList.FirstOrDefault()?.Id;
        if (targetId is null) return;   // 系统里还没有员工，或者筛选条件没搜到人

        // id 是 URL 上的查询参数，受限管理员完全可以直接改 URL 里的 id 尝试查看别的分公司员工的
        // 完整资料（身份证号/住址/紧急联系人等敏感信息）——候选名单已经按范围过滤了，但这里还要
        // 再单独校验一次目标员工本身在不在范围内，不能只信候选名单挡住了界面上的入口
        var target = await userService.GetUserWithDetailsAsync(targetId.Value);
        if (target is not null && await deptScopeService.CanAccessDeptAsync(cu, target.DepartmentId))
            Employee = target;
    }

    /// <summary>按当前筛选条件（部门/关键字），把匹配到的全部员工基础资料导出成 Excel。</summary>
    public async Task<IActionResult> OnGetExportAsync(string? keyword, int? deptId)
    {
        var cu = HttpContext.GetCurrentUser()!;
        deptId = await deptScopeService.ResolveEffectiveDeptIdAsync(cu, deptId);
        var (users, _) = await userService.GetUsersAsync(deptId: deptId, keyword: keyword, pageIndex: 1, pageSize: 100_000);
        var bytes = ExcelExportHelper.ExportEmployeeList(users);

        // 导出内容含身份证号/住址/手机号这类完整个人信息，留一条审计日志（谁在什么时候导出了多少条），
        // 出了信息泄露纠纷至少能查到是谁导出的——这里只记操作留痕，不改变导出内容本身
        // （导出内容要不要脱敏是业务取舍：脱敏了这份导出可能就没法直接拿去做工资/社保申报这些
        // 本来就需要完整身份证号的用途，这个决定应该由业务负责人来定，不是这次顺手改掉的事）
        var operatorNo = User.FindFirstValue(ClaimTypes.Name) ?? "?";
        logger.LogInformation("管理员 {OperatorNo} 导出了员工信息 Excel，共 {Count} 条（筛选条件：部门={DeptId}，关键字={Keyword}）",
            operatorNo, users.Count, deptId, keyword);

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"员工信息_{DateTime.Now:yyyyMMdd}.xlsx");   // 不能再 UrlEncode：File() 会自己处理中文文件名，先编码一次会变成 %e5%91%98… 的乱码
    }

    /// <summary>加载部门下拉选项：按"父部门在前、子部门缩进"的顺序摊平成一份列表；受限管理员只看到
    /// 自己范围内的部门。</summary>
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
