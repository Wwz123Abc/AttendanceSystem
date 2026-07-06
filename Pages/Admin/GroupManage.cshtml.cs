using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>考勤组管理页：考勤组的新增/修改/启停，并统计各组在职人数。支持“按部门同步”一键建组+归属。</summary>
[Authorize(Policy = "ManagePolicy")]
public class GroupManageModel(AttendanceDbContext db, IAttendanceGroupService groupService) : PageModel
{
    // 列表里每项是 (考勤组, 该组在职人数)
    public List<(AttendanceGroup Group, int UserCount)> Groups { get; set; } = [];
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    // 表单字段（EditId=0 表示新增，否则是修改）
    [BindProperty] public int     EditId         { get; set; }
    [BindProperty] public string  GroupName      { get; set; } = "";
    [BindProperty] public string? CompanyName    { get; set; }
    [BindProperty] public int     PunchRadius    { get; set; } = 500;
    [BindProperty] public double? Latitude       { get; set; }
    [BindProperty] public double? Longitude      { get; set; }
    [BindProperty] public string? LocationName   { get; set; }
    [BindProperty] public bool    EnableLocation { get; set; }
    [BindProperty] public int     LunchBreak     { get; set; } = 60;
    [BindProperty] public int     DinnerBreak    { get; set; } = 30;

    /// <summary>打开页面：列出所有考勤组，并算出每组的在职人数。</summary>
    public async Task OnGetAsync()
    {
        var groups = await db.AttendanceGroups
            .OrderBy(g => g.GroupName)
            .ToListAsync();
        // 按考勤组分组，数出每组多少在职人
        var userCounts = await db.Users
            .Where(u => u.IsActive && u.AttendanceGroupId != null)
            .GroupBy(u => u.AttendanceGroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToListAsync();

        // 把考勤组和人数配对起来
        Groups = groups.Select(g => (g, userCounts.FirstOrDefault(c => c.GroupId == g.Id)?.Count ?? 0)).ToList();
    }

    /// <summary>点“保存”：新增或修改考勤组。</summary>
    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(GroupName)) { ErrorMessage = "考勤组名称不能为空"; await OnGetAsync(); return Page(); }

        try
        {
            if (EditId == 0)   // 新增
            {
                db.AttendanceGroups.Add(new AttendanceGroup
                {
                    GroupName           = GroupName.Trim(),
                    CompanyName         = CompanyName?.Trim(),
                    PunchRadiusMeters   = PunchRadius,
                    LocationLatitude    = Latitude,
                    LocationLongitude   = Longitude,
                    LocationName        = LocationName?.Trim(),
                    EnableLocationPunch = EnableLocation,
                    LunchBreakMinutes   = LunchBreak,
                    DinnerBreakMinutes  = DinnerBreak,
                    IsActive            = true,
                    CreatedAt           = DateTime.Now,
                    UpdatedAt           = DateTime.Now
                });
                SuccessMessage = $"考勤组「{GroupName}」已创建";
            }
            else   // 修改
            {
                var g = await db.AttendanceGroups.FindAsync(EditId);
                if (g is not null)
                {
                    g.GroupName           = GroupName.Trim();
                    g.CompanyName         = CompanyName?.Trim();
                    g.PunchRadiusMeters   = PunchRadius;
                    g.LocationLatitude    = Latitude;
                    g.LocationLongitude   = Longitude;
                    g.LocationName        = LocationName?.Trim();
                    g.EnableLocationPunch = EnableLocation;
                    g.LunchBreakMinutes   = LunchBreak;
                    g.DinnerBreakMinutes  = DinnerBreak;
                    g.UpdatedAt           = DateTime.Now;
                }
                SuccessMessage = $"考勤组「{GroupName}」已更新";
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        return RedirectToPage();
    }

    /// <summary>点“按部门同步”：给每个部门补建考勤组，并把有部门的员工归入其部门对应的考勤组。</summary>
    public async Task<IActionResult> OnPostSyncDeptGroupsAsync()
    {
        try
        {
            var (created, moved) = await groupService.SyncAllAsync();
            SuccessMessage = $"按部门同步完成：新建考勤组 {created} 个，调整员工归属 {moved} 人";
        }
        catch (Exception ex) { ErrorMessage = "同步失败：" + ex.Message; }
        await OnGetAsync();
        return Page();
    }

    /// <summary>点“启用/停用”：切换某考勤组的启停状态。</summary>
    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var g = await db.AttendanceGroups.FindAsync(id);
        if (g is not null) { g.IsActive = !g.IsActive; g.UpdatedAt = DateTime.Now; await db.SaveChangesAsync(); }
        return RedirectToPage();
    }
}
