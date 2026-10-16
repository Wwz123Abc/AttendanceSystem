using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 饭点设置页：<b>按考勤组</b>设置"不算钱"的饭点时段（默认 12:00-13:00、17:30-18:00、00:00-01:00、05:30-06:00）。
/// 加班、请假、每天的实际工时，起止时间压到这几段的部分不计入时长，所以这直接影响工资工时，
/// 只有不受部门范围限制的总部管理员能改；管理员/文员都能看。改完只影响之后的计算和重新计算，已经存好的工时不会自己变。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class MealBreakSettingsModel(AttendanceDbContext db, ILogger<MealBreakSettingsModel> logger) : PageModel
{
    [BindProperty] public int GroupId { get; set; }
    [BindProperty] public List<string> Starts { get; set; } = [];
    [BindProperty] public List<string> Ends   { get; set; } = [];

    public record GroupRow(int Id, string Name, int Users, bool Customized, string Windows);
    public List<GroupRow> Groups { get; set; } = [];
    public string GroupName { get; set; } = "";

    public bool CanEdit { get; set; }
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }
    public string? UpdatedInfo    { get; set; }
    public bool IsDefault { get; set; }

    public async Task OnGetAsync(int? groupId)
    {
        GroupId = groupId ?? 0;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        CanEdit = HttpContext.GetRequiredUser().IsHqSuperAdmin;
        if (!CanEdit) { ErrorMessage = "只有总部管理员能修改饭点时段"; await LoadAsync(); return Page(); }
        if (!await db.AttendanceGroups.AnyAsync(g => g.Id == GroupId)) { ErrorMessage = "请先选择考勤组"; await LoadAsync(); return Page(); }

        var windows = new List<(TimeOnly, TimeOnly)>();
        for (var i = 0; i < Math.Max(Starts.Count, Ends.Count); i++)
        {
            var s = i < Starts.Count ? Starts[i] : "";
            var e = i < Ends.Count ? Ends[i] : "";
            if (string.IsNullOrWhiteSpace(s) && string.IsNullOrWhiteSpace(e)) continue;   // 整行留空 = 这行不要了
            if (!TimeOnly.TryParse(s, out var st) || !TimeOnly.TryParse(e, out var en))
            {
                ErrorMessage = $"第 {i + 1} 行的开始/结束时间没填完整或格式不对";
                await LoadAsync(keepForm: true);
                return Page();
            }
            windows.Add((st, en));
        }
        var problem = UnpaidBreakSettings.Validate(windows);
        if (problem is not null) { ErrorMessage = problem; await LoadAsync(keepForm: true); return Page(); }

        await SaveAsync(GroupId, windows);
        SuccessMessage = windows.Count == 0
            ? "已保存：这个考勤组现在没有任何饭点时段，加班申请的时长按起止时间全额计算"
            : $"已保存：{UnpaidBreakSettings.Describe(GroupId)}。这个考勤组之后新提交的加班申请会按这个算；正常上班工时和请假不受影响。";
        await LoadAsync();
        return Page();
    }

    /// <summary>这个考勤组恢复默认时段（删掉单独设置）。</summary>
    public async Task<IActionResult> OnPostResetAsync()
    {
        CanEdit = HttpContext.GetRequiredUser().IsHqSuperAdmin;
        if (!CanEdit) { ErrorMessage = "只有总部管理员能修改饭点时段"; await LoadAsync(); return Page(); }
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == UnpaidBreakSettings.KeyFor(GroupId));
        if (row is not null) { db.SystemSettings.Remove(row); await db.SaveChangesAsync(); }
        UnpaidBreakSettings.Remove(GroupId);
        logger.LogInformation("管理员 {Operator} 把考勤组 {GroupId} 的饭点时段恢复成了默认", User.FindFirstValue(ClaimTypes.Name) ?? "?", GroupId);
        SuccessMessage = $"已恢复默认：{UnpaidBreakSettings.Describe(GroupId)}";
        await LoadAsync();
        return Page();
    }

    private async Task SaveAsync(int groupId, List<(TimeOnly Start, TimeOnly End)> windows)
    {
        var key = UnpaidBreakSettings.KeyFor(groupId);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) db.SystemSettings.Add(row = new SystemSetting { Key = key });
        row.Value = UnpaidBreakSettings.Format(windows);
        row.UpdatedAt = DateTime.Now;
        row.UpdatedBy = User.FindFirstValue("RealName");
        await db.SaveChangesAsync();
        UnpaidBreakSettings.Apply(groupId, windows);
        logger.LogInformation("管理员 {Operator} 修改了考勤组 {GroupId} 的饭点时段：{Windows}", User.FindFirstValue(ClaimTypes.Name) ?? "?", groupId, row.Value);
    }

    private async Task LoadAsync(bool keepForm = false)
    {
        CanEdit = HttpContext.GetRequiredUser().IsHqSuperAdmin;
        var userCounts = await db.Users.Where(u => u.IsActive && u.AttendanceGroupId != null)
            .GroupBy(u => u.AttendanceGroupId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        Groups = (await db.AttendanceGroups.OrderBy(g => g.GroupName).Select(g => new { g.Id, g.GroupName }).ToListAsync())
            .Select(g => new GroupRow(g.Id, g.GroupName, userCounts.GetValueOrDefault(g.Id), UnpaidBreakSettings.IsCustomized(g.Id), UnpaidBreakSettings.Describe(g.Id)))
            .ToList();
        if (Groups.All(g => g.Id != GroupId)) GroupId = Groups.FirstOrDefault()?.Id ?? 0;   // 没选/选的不存在：默认第一个考勤组
        GroupName = Groups.FirstOrDefault(g => g.Id == GroupId)?.Name ?? "";

        var row = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == UnpaidBreakSettings.KeyFor(GroupId));
        UpdatedInfo = row is null ? null : $"最近修改：{row.UpdatedAt:yyyy-MM-dd HH:mm}（{row.UpdatedBy}）";
        IsDefault = !UnpaidBreakSettings.IsCustomized(GroupId);
        if (keepForm) return;   // 校验没通过：保留管理员刚填的内容，让他改
        ModelState.Clear();
        var w = UnpaidBreakSettings.WindowsFor(GroupId);
        Starts = [.. w.Select(x => x.Start.ToString("HH:mm"))];
        Ends   = [.. w.Select(x => x.End.ToString("HH:mm"))];
    }
}
