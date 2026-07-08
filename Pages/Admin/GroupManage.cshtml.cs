using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 考勤组管理页：考勤组的新增/修改/启停，统计各组在职人数；
/// 支持配一个或多个打卡地点、勾选长期跟随本组的部门（替代以前"一个部门一个考勤组"的自动同步）。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class GroupManageModel(AttendanceDbContext db, IAttendanceGroupService groupService) : PageModel
{
    // 列表里每项是 (考勤组, 该组在职人数)
    public List<(AttendanceGroup Group, int UserCount)> Groups { get; set; } = [];
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    /// <summary>可选审批人（在职、非普通员工），供多选框列出</summary>
    public List<User> ApproverOptions { get; set; } = [];
    /// <summary>每个考勤组已配置的审批人编号：key=考勤组Id，value=审批人Id列表</summary>
    public Dictionary<int, List<int>> GroupApproverIds { get; set; } = [];

    /// <summary>部门树（扁平化，带层级深度），供"所属部门"勾选框展示</summary>
    public List<DeptTreeNode> DeptTree { get; set; } = [];
    /// <summary>每个考勤组已勾选跟随的部门编号：key=考勤组Id，value=部门Id列表</summary>
    public Dictionary<int, List<int>> GroupDepartmentIds { get; set; } = [];
    /// <summary>每个考勤组配置的打卡地点：key=考勤组Id</summary>
    public Dictionary<int, List<AttendanceGroupLocation>> GroupLocations { get; set; } = [];

    /// <summary>部门树的一个节点：部门本身信息 + 层级深度 + 当前跟随的考勤组（可能不是本次在编辑的这个）</summary>
    public record DeptTreeNode(int Id, int? ParentId, string Name, int Depth, int? AttendanceGroupId);

    // 表单字段（EditId=0 表示新增，否则是修改）
    [BindProperty] public int     EditId         { get; set; }
    [BindProperty] public string  GroupName      { get; set; } = "";
    [BindProperty] public bool    EnableLocation { get; set; }
    [BindProperty] public int     LunchBreak     { get; set; } = 60;
    [BindProperty] public int     DinnerBreak    { get; set; } = 30;
    /// <summary>本次勾选的审批人编号列表（可以一个都不选，届时退回直属上级/兜底管理员）</summary>
    [BindProperty] public List<int> ApproverUserIds { get; set; } = [];
    /// <summary>本次勾选的部门编号列表（长期跟随本组）</summary>
    [BindProperty] public List<int> SelectedDeptIds { get; set; } = [];
    /// <summary>本次填写的打卡地点列表（可以一个都不填，届时视为不限制定位）</summary>
    [BindProperty] public List<LocationInput> Locations { get; set; } = [];

    /// <summary>表单里一个"打卡地点"行对应的数据。</summary>
    public class LocationInput
    {
        public string?  Name      { get; set; }
        public double?  Latitude  { get; set; }
        public double?  Longitude { get; set; }
        public int      Radius    { get; set; } = 500;
    }

    /// <summary>打开页面：列出所有考勤组、每组在职人数/审批人/所属部门/打卡地点，及可选的审批人、部门树。</summary>
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

        // 能当审批人的人：在职、角色不是普通员工（和以前"审批流程管理"页的筛选口径一致）
        ApproverOptions = await db.Users
            .Where(u => u.IsActive && u.Role != Models.Enums.UserRole.Employee)
            .OrderBy(u => u.RealName)
            .ToListAsync();

        // 每个考勤组已经配了哪些审批人，用于编辑时勾选回显
        GroupApproverIds = (await db.AttendanceGroupApprovers.ToListAsync())
            .GroupBy(a => a.AttendanceGroupId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.UserId).ToList());

        // 每个考勤组配置的打卡地点
        GroupLocations = (await db.AttendanceGroupLocations.ToListAsync())
            .GroupBy(l => l.AttendanceGroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        await LoadDeptTreeAsync();
    }

    /// <summary>加载部门树（扁平化+层级深度），并按跟随的考勤组分组，供勾选框回显和"所属部门"列展示。</summary>
    private async Task LoadDeptTreeAsync()
    {
        var depts   = await db.Departments.Where(d => d.IsActive).OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
        var byParent = depts.GroupBy(d => d.ParentId ?? 0).ToDictionary(g => g.Key, g => g.ToList());

        DeptTree = [];
        void Walk(int parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var kids)) return;
            foreach (var d in kids)
            {
                DeptTree.Add(new DeptTreeNode(d.Id, d.ParentId, d.DeptName, depth, d.AttendanceGroupId));
                Walk(d.Id, depth + 1);
            }
        }
        Walk(0, 0);

        GroupDepartmentIds = depts
            .Where(d => d.AttendanceGroupId.HasValue)
            .GroupBy(d => d.AttendanceGroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Id).ToList());
    }

    /// <summary>点“保存”：新增或修改考勤组。</summary>
    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(GroupName)) { ErrorMessage = "考勤组名称不能为空"; await OnGetAsync(); return Page(); }

        try
        {
            AttendanceGroup? g;
            if (EditId == 0)   // 新增
            {
                g = new AttendanceGroup
                {
                    GroupName           = GroupName.Trim(),
                    EnableLocationPunch = EnableLocation,
                    LunchBreakMinutes   = LunchBreak,
                    DinnerBreakMinutes  = DinnerBreak,
                    IsActive            = true,
                    CreatedAt           = DateTime.Now,
                    UpdatedAt           = DateTime.Now
                };
                db.AttendanceGroups.Add(g);
                await db.SaveChangesAsync();   // 先存一次，拿到新考勤组的 Id，后面配地点/审批人/部门要用
                SuccessMessage = $"考勤组「{GroupName}」已创建";
            }
            else   // 修改
            {
                g = await db.AttendanceGroups.FindAsync(EditId);
                if (g is not null)
                {
                    g.GroupName           = GroupName.Trim();
                    g.EnableLocationPunch = EnableLocation;
                    g.LunchBreakMinutes   = LunchBreak;
                    g.DinnerBreakMinutes  = DinnerBreak;
                    g.UpdatedAt           = DateTime.Now;
                }
                SuccessMessage = $"考勤组「{GroupName}」已更新";
            }

            if (g is not null)
            {
                // 同步打卡地点：先清空这个组原来配的，再按这次填写的重新加入（跳过没填全经纬度的行）
                var oldLocations = await db.AttendanceGroupLocations
                    .Where(l => l.AttendanceGroupId == g.Id).ToListAsync();
                db.AttendanceGroupLocations.RemoveRange(oldLocations);
                foreach (var loc in Locations)
                {
                    if (!loc.Latitude.HasValue || !loc.Longitude.HasValue) continue;
                    db.AttendanceGroupLocations.Add(new AttendanceGroupLocation
                    {
                        AttendanceGroupId = g.Id,
                        LocationName      = string.IsNullOrWhiteSpace(loc.Name) ? null : loc.Name.Trim(),
                        Latitude          = loc.Latitude.Value,
                        Longitude         = loc.Longitude.Value,
                        RadiusMeters      = loc.Radius <= 0 ? 500 : loc.Radius
                    });
                }

                // 同步审批人名单：先清空这个组原来配的，再按这次勾选的重新加入
                var oldApprovers = await db.AttendanceGroupApprovers
                    .Where(a => a.AttendanceGroupId == g.Id).ToListAsync();
                db.AttendanceGroupApprovers.RemoveRange(oldApprovers);
                foreach (var uid in ApproverUserIds.Distinct())
                    db.AttendanceGroupApprovers.Add(new AttendanceGroupApprover
                    {
                        AttendanceGroupId = g.Id,
                        UserId            = uid
                    });

                await db.SaveChangesAsync();

                // 同步跟随部门：解除没勾的、关联新勾的，并立即把这些部门现有员工批量归组
                var moved = await groupService.SetGroupDepartmentsAsync(g.Id, SelectedDeptIds);
                if (moved > 0) SuccessMessage += $"，同步归组 {moved} 名员工";
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        return RedirectToPage();
    }

    /// <summary>点“启用/停用”：切换某考勤组的启停状态。</summary>
    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var g = await db.AttendanceGroups.FindAsync(id);
        if (g is not null) { g.IsActive = !g.IsActive; g.UpdatedAt = DateTime.Now; await db.SaveChangesAsync(); }
        return RedirectToPage();
    }

    /// <summary>
    /// 点“删除”：彻底删除该考勤组。删除后：组内员工的“考勤组”自动清空（不会删员工，
    /// 需要另外分配到别的组）；跟随本组的部门解除跟随关系；本组的班次/排班记录、审批人名单、
    /// 打卡地点会连带一起删除，不可恢复。
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        try
        {
            var g = await db.AttendanceGroups.FindAsync(id);
            if (g is null) { ErrorMessage = "该考勤组不存在"; }
            else
            {
                var userCount = await db.Users.CountAsync(u => u.AttendanceGroupId == id);
                var name = g.GroupName;
                db.AttendanceGroups.Remove(g);
                await db.SaveChangesAsync();
                SuccessMessage = $"考勤组「{name}」已删除" + (userCount > 0 ? $"，原有 {userCount} 名员工已解除该考勤组归属" : "");
            }
        }
        catch (Exception ex) { ErrorMessage = "删除失败：" + ex.Message; }
        return RedirectToPage();
    }
}
