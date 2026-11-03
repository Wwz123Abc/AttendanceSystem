using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 数据核对页：① 上方是"本周每晚核对留档"——每天夜里自动核对（04:00）把疑点存下来，这里按当前周展示；
/// 每周日 23:30 自动清掉上周的，只保留当前周（周一~周日）。② 下方是实时核对：选一段日期，按现行规则重算一遍
/// 和存库的工时/加班对比，把对不上的和缺前提的列出来。实时核对只读，不改任何数据。
/// 分公司管理员只看到自己范围内员工的疑点（2026-10-22 新增）。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class DataAuditModel(IAttendanceService attendanceService, IDeptScopeService deptScopeService, AttendanceDbContext db, TimeProvider timeProvider) : PageModel
{
    private const int MaxRows = 500;
    private const int MaxLogRows = 300;

    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public string? TypeFilter { get; set; }

    /// <summary>本周一（周一~周日算一周）。</summary>
    public DateOnly WeekStart { get; set; }

    // 实时核对
    public List<AuditIssue> Issues { get; set; } = [];
    public int TotalIssues { get; set; }
    public List<(string Type, string Severity, int Count)> Summary { get; set; } = [];
    public bool Truncated => TotalIssues > Issues.Count;

    // 每晚核对留档（当前周）
    public List<(DateOnly RunDate, int Count, string ByType)> WeeklyLogSummary { get; set; } = [];
    public List<AuditIssueLog> WeeklyLogs { get; set; } = [];
    public int WeeklyLogTotal { get; set; }
    public bool WeeklyLogTruncated => WeeklyLogTotal > WeeklyLogs.Count;

    public async Task OnGetAsync(DateOnly? start, DateOnly? end, string? type)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        WeekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // 周一=0、周日=6

        // 实时核对默认只跑当前周（跟"只保留当前周数据"的口径一致；要看更长区间可以自己改日期，最长 63 天）
        End = end ?? today;
        Start = start ?? WeekStart;
        if (End < Start) (Start, End) = (End, Start);
        if (End.DayNumber - Start.DayNumber > 62) Start = End.AddDays(-62);   // 核对要重算每条记录，范围太长会很慢
        TypeFilter = string.IsNullOrWhiteSpace(type) ? null : type;

        // 分公司管理员只看自己范围内的人（实时核对和留档都要过滤）
        var visible = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetRequiredUser());
        HashSet<int>? allowed = null;
        if (visible is not null)
            allowed = (await db.Users
                    .Where(u => u.DepartmentId != null && visible.Contains(u.DepartmentId.Value))
                    .Select(u => u.Id).ToListAsync())
                .ToHashSet();

        // ① 每晚核对留档（当前周）
        var logs = await attendanceService.GetAuditIssueLogsAsync(WeekStart, today, HttpContext.RequestAborted);
        if (allowed is not null) logs = logs.Where(l => allowed.Contains(l.UserId)).ToList();
        WeeklyLogTotal = logs.Count;
        WeeklyLogSummary = logs.GroupBy(l => l.RunDate)
            .Select(g => (RunDate: g.Key, Count: g.Count(),
                ByType: string.Join("、", g.GroupBy(x => x.IssueType).OrderByDescending(t => t.Count())
                    .Select(t => $"{t.Key} {t.Count()}"))))
            .OrderByDescending(x => x.RunDate).ToList();
        WeeklyLogs = logs.Take(MaxLogRows).ToList();

        // ② 实时核对
        var all = await attendanceService.AuditAsync(Start, End, HttpContext.RequestAborted);
        if (allowed is not null) all = all.Where(i => allowed.Contains(i.UserId)).ToList();

        Summary = all.GroupBy(i => (i.Type, i.Severity)).Select(g => (g.Key.Type, g.Key.Severity, g.Count()))
            .OrderBy(x => x.Severity == "danger" ? 0 : 1).ThenByDescending(x => x.Item3).ToList();
        var filtered = TypeFilter is null ? all : all.Where(i => i.Type == TypeFilter).ToList();
        TotalIssues = filtered.Count;
        Issues = filtered.Take(MaxRows).ToList();
    }
}
