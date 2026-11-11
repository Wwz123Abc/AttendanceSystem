using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 排班修改记录：谁、什么时候、把谁的哪一天、从什么班次改成什么班次。
/// 2026-11-11 用户要求（之前被批量排班覆盖后查不到人）。只给有排班管理权限的人看，
/// 分公司管理员只能看自己部门范围内员工的记录。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class ShiftLogModel(AttendanceDbContext db, IDeptScopeService deptScopeService) : PageModel
{
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Keyword { get; set; }

    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly EffectiveTo { get; private set; }
    public List<Row> Rows { get; private set; } = [];

    public record Row(int Id, DateOnly WorkDate, string EmployeeNo, string RealName,
        string? OldShift, string? NewShift, string Action, string Source,
        string? Operator, DateTime CreatedAt, string? Remark);

    public async Task OnGetAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        EffectiveFrom = From ?? today.AddDays(-7);
        EffectiveTo   = To ?? today;
        if (EffectiveTo < EffectiveFrom) (EffectiveFrom, EffectiveTo) = (EffectiveTo, EffectiveFrom);
        var kw = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();

        // 按"操作时间"筛，不是按考勤日：批量排班常常一次改到未来的日期（比如今天排 12 月），
        // 按考勤日筛的话默认"最近 7 天"根本看不到这些刚做的修改（2026-11-11 自查发现）。
        var fromDt = EffectiveFrom.ToDateTime(TimeOnly.MinValue);
        var toDtExclusive = EffectiveTo.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var visible = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetRequiredUser());
        Rows = await (
            from l in db.ShiftAssignmentLogs.AsNoTracking()
            join u in db.Users on l.UserId equals u.Id
            where l.CreatedAt >= fromDt && l.CreatedAt < toDtExclusive
                  && (visible == null || (u.DepartmentId != null && visible.Contains(u.DepartmentId.Value)))
                  && (kw == null || u.RealName.Contains(kw) || u.EmployeeNo.Contains(kw))
            orderby l.Id descending
            select new Row(l.Id, l.WorkDate, u.EmployeeNo, u.RealName,
                l.OldShiftName, l.NewShiftName, l.Action, l.Source, l.OperatorName, l.CreatedAt, l.Remark))
            .Take(1000)
            .ToListAsync();
    }
}
