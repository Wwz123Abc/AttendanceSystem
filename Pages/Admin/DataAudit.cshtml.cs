using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 数据核对页：选一段日期，系统把这段时间每条考勤记录按现行规则重新算一遍，和存库的工时、加班对比，
/// 把对不上的（工资可能算错）和缺前提的（比如有打卡但没排班）列出来。只读，不改任何数据；
/// 分公司管理员只看到自己范围内员工的疑点（2026-10-22 新增）。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class DataAuditModel(IAttendanceService attendanceService, IDeptScopeService deptScopeService, AttendanceDbContext db, TimeProvider timeProvider) : PageModel
{
    private const int MaxRows = 500;

    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public string? TypeFilter { get; set; }

    public List<AuditIssue> Issues { get; set; } = [];
    public int TotalIssues { get; set; }
    public List<(string Type, string Severity, int Count)> Summary { get; set; } = [];
    public bool Truncated => TotalIssues > Issues.Count;

    public async Task OnGetAsync(DateOnly? start, DateOnly? end, string? type)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        End = end ?? today;
        Start = start ?? today.AddDays(-14);
        if (End < Start) (Start, End) = (End, Start);
        if (End.DayNumber - Start.DayNumber > 62) Start = End.AddDays(-62);   // 核对要重算每条记录，范围太长会很慢
        TypeFilter = string.IsNullOrWhiteSpace(type) ? null : type;

        var all = await attendanceService.AuditAsync(Start, End, HttpContext.RequestAborted);

        // 分公司管理员只看自己范围内的人
        var visible = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetRequiredUser());
        if (visible is not null)
        {
            var ids = all.Select(i => i.UserId).Distinct().ToList();
            var allowed = (await db.Users.Where(u => ids.Contains(u.Id) && u.DepartmentId != null && visible.Contains(u.DepartmentId.Value))
                .Select(u => u.Id).ToListAsync()).ToHashSet();
            all = all.Where(i => allowed.Contains(i.UserId)).ToList();
        }

        Summary = all.GroupBy(i => (i.Type, i.Severity)).Select(g => (g.Key.Type, g.Key.Severity, g.Count()))
            .OrderBy(x => x.Severity == "danger" ? 0 : 1).ThenByDescending(x => x.Item3).ToList();
        var filtered = TypeFilter is null ? all : all.Where(i => i.Type == TypeFilter).ToList();
        TotalIssues = filtered.Count;
        Issues = filtered.Take(MaxRows).ToList();
    }
}
