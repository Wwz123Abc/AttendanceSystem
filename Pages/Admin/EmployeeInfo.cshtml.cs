using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 员工信息页：把系统里已知的某个员工的全部资料——基本信息、组织信息、联系方式、证件、
/// 紧急联系人、合同/入职信息、账号状态——整合到一页展示，方便管理员一次看全、也方便打印存档。
/// 只读页面，不提供编辑（编辑仍在"员工管理"页完成，避免同一份资料维护两套表单）。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class EmployeeInfoModel(IUserService userService, AttendanceDbContext db) : PageModel
{
    public User?   Employee { get; set; }
    public string? Keyword  { get; set; }

    /// <summary>左侧"切换员工"用的候选名单：按关键字过滤，最多取 20 条，避免几千号人一次性全部列出来。</summary>
    public List<User> QuickList { get; set; } = [];

    public async Task OnGetAsync(int? id, string? keyword)
    {
        Keyword = keyword;

        var listQuery = db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(keyword))
            listQuery = listQuery.Where(u => u.RealName.Contains(keyword) || u.EmployeeNo.Contains(keyword));
        QuickList = await listQuery.OrderByDescending(u => u.IsActive).ThenBy(u => u.EmployeeNo).Take(20).ToListAsync();

        // 没指定要看谁，就默认看候选名单里的第一个（比如刚搜索出来的那批人）
        var targetId = id ?? QuickList.FirstOrDefault()?.Id;
        if (targetId is null) return;   // 系统里还没有员工，或者关键字没搜到人

        Employee = await userService.GetUserWithDetailsAsync(targetId.Value);
    }
}
