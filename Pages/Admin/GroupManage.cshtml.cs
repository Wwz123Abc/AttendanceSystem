using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 考勤组管理页：考勤组的新增/修改/启停，统计各组在职人数；
/// 支持配一个或多个打卡地点、勾选长期跟随本组的部门（替代以前"一个部门一个考勤组"的自动同步）。
/// 分公司管理员只能看到/管理"关联部门落在自己范围内"的考勤组（还没关联任何部门的组，当成
/// 全公司通用组，所有管理员都能看到——跟节假日管理里"不挂考勤组的假期全公司通用"是同一个口径）。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class GroupManageModel(
    AttendanceDbContext db,
    IAttendanceGroupService groupService,
    IDeptScopeService deptScopeService,
    IOptions<AMapOptions> amapOptions,
    ILogger<GroupManageModel> logger) : PageModel
{
    /// <summary>高德地图 Web端(JS API) Key（配置了才会在页面上加载地图选点功能）。</summary>
    public string AMapJsKey => amapOptions.Value.JsApiKey;
    /// <summary>高德地图安全密钥（配套 Key 一起用）。</summary>
    public string AMapSecurityCode => amapOptions.Value.SecurityJsCode;
    /// <summary>是否已配置好高德地图 Key，决定"地图选点"按钮要不要显示。</summary>
    public bool AMapEnabled => !string.IsNullOrWhiteSpace(AMapJsKey);

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
    /// <summary>本次勾选的审批人编号列表（必须至少选一个）</summary>
    [BindProperty] public List<int> ApproverUserIds { get; set; } = [];
    /// <summary>本次选择的审批层级："Level1"=一级（仅班组长），"Level2"=二级（班组长+直属上级）</summary>
    [BindProperty] public string ApprovalLevel { get; set; } = "Level1";
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
        var cu = HttpContext.GetCurrentUser()!;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);

        var groups = await db.AttendanceGroups.Include(g => g.Departments)
            .OrderBy(g => g.GroupName)
            .ToListAsync();
        if (visibleIds is not null)
            groups = groups.Where(g => g.Departments.Count == 0 || g.Departments.Any(d => visibleIds.Contains(d.Id))).ToList();

        // 按考勤组分组，数出每组多少在职人
        var userCounts = await db.Users
            .Where(u => u.IsActive && u.AttendanceGroupId != null)
            .GroupBy(u => u.AttendanceGroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToListAsync();

        // 把考勤组和人数配对起来
        Groups = groups.Select(g => (g, userCounts.FirstOrDefault(c => c.GroupId == g.Id)?.Count ?? 0)).ToList();

        // 能当审批人的人：以后新增只能选"班组长"角色（一级审批人）；
        // 但存量已经配置成审批人的历史数据（哪怕角色不是班组长）也要留在候选名单里，
        // 保证编辑这个组的其它字段、保存时不会把这些人悄悄清掉——只有管理员自己手动取消勾选才会移除。
        // 受限管理员只能选自己范围内的人当审批人（不能跨分公司指定审批人）。
        var legacyApproverIds = await db.AttendanceGroupApprovers.Select(a => a.UserId).Distinct().ToListAsync();
        var approverQuery = db.Users
            .Where(u => u.IsActive && (u.Role == Models.Enums.UserRole.TeamLeader || legacyApproverIds.Contains(u.Id)));
        if (visibleIds is not null)
            approverQuery = approverQuery.Where(u => u.DepartmentId != null && visibleIds.Contains(u.DepartmentId.Value));
        ApproverOptions = await approverQuery.OrderBy(u => u.RealName).ToListAsync();

        // 每个考勤组已经配了哪些审批人，用于编辑时勾选回显
        GroupApproverIds = (await db.AttendanceGroupApprovers.ToListAsync())
            .GroupBy(a => a.AttendanceGroupId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.UserId).ToList());

        // 每个考勤组配置的打卡地点
        GroupLocations = (await db.AttendanceGroupLocations.ToListAsync())
            .GroupBy(l => l.AttendanceGroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        await LoadDeptTreeAsync(cu, visibleIds);
    }

    /// <summary>加载部门树（扁平化+层级深度），并按跟随的考勤组分组，供勾选框回显和"所属部门"列展示；
    /// 受限管理员只看到自己范围内的部门。</summary>
    private async Task LoadDeptTreeAsync(CurrentUser cu, HashSet<int>? visibleIds)
    {
        var depts = await db.Departments.Where(d => d.IsActive).OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
        if (visibleIds is not null)
            depts = depts.Where(d => visibleIds.Contains(d.Id)).ToList();
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
        if (cu.IsScoped)
        {
            var root = depts.FirstOrDefault(d => d.Id == cu.ScopedDepartmentId!.Value);
            if (root is not null)
            {
                DeptTree.Add(new DeptTreeNode(root.Id, root.ParentId, root.DeptName, 0, root.AttendanceGroupId));
                Walk(root.Id, 1);
            }
        }
        else
        {
            Walk(0, 0);
        }

        GroupDepartmentIds = depts
            .Where(d => d.AttendanceGroupId.HasValue)
            .GroupBy(d => d.AttendanceGroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Id).ToList());
    }

    /// <summary>某个考勤组是否在当前登录者的管理范围内：不受限一律可以；受限的话，看这个组关联的部门
    /// 有没有落在自己范围内——一个部门都没关联（还没分配给任何分公司）的组，当成全公司通用组，
    /// 所有管理员都能看到/编辑（跟看板/节假日那边的口径一致）。</summary>
    private async Task<bool> IsGroupInScopeAsync(CurrentUser cu, int groupId)
    {
        if (!cu.IsScoped) return true;
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return true;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
        return deptIds.Any(id => visibleIds!.Contains(id));
    }

    /// <summary>某个考勤组是否允许当前登录者"写"（修改/停用/删除这个组本身）：不受限一律可以；
    /// 受限管理员只能写"关联部门都在自己范围内"的组——完全没关联部门的全局/共享考勤组（可能有多个
    /// 分公司在用）只有总部管理员能改，避免分公司管理员动了别的分公司也在用的共用配置（可见，但不能
    /// 改，跟上面 IsGroupInScopeAsync 的"能看到"口径区分开）。</summary>
    private async Task<bool> IsGroupWritableAsync(CurrentUser cu, int groupId)
    {
        if (!cu.IsScoped) return true;
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return false;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
        // 必须是"关联部门都在自己范围内"才能写（All，不是 Any）——否则跨司共用组会变成双方受限管理员都能改
        return deptIds.All(id => visibleIds!.Contains(id));
    }

    /// <summary>点“保存”：新增或修改考勤组。</summary>
    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(GroupName)) { ErrorMessage = "考勤组名称不能为空"; await OnGetAsync(); return Page(); }
        if (GroupName.Trim().Length > 100) { ErrorMessage = "考勤组名称不能超过 100 个字"; await OnGetAsync(); return Page(); }
        if (LunchBreak is < 0 or > 120) { ErrorMessage = "午休时长请填 0-120 分钟之间"; await OnGetAsync(); return Page(); }
        if (DinnerBreak is < 0 or > 120) { ErrorMessage = "晚餐时长请填 0-120 分钟之间"; await OnGetAsync(); return Page(); }
        if (ApproverUserIds.Count == 0) { ErrorMessage = "请至少选择一位审批人"; await OnGetAsync(); return Page(); }
        if (!Enum.TryParse<Models.Enums.ApprovalLevelType>(ApprovalLevel, out var approvalLevel)) approvalLevel = Models.Enums.ApprovalLevelType.Level1;
        foreach (var loc in Locations)
        {
            if (!loc.Latitude.HasValue || !loc.Longitude.HasValue) continue;   // 没填全经纬度的行本来就会被跳过，不用校验
            if (loc.Latitude.Value is < -90 or > 90) { ErrorMessage = "打卡地点纬度不正确（应在 -90 到 90 之间）"; await OnGetAsync(); return Page(); }
            if (loc.Longitude.Value is < -180 or > 180) { ErrorMessage = "打卡地点经度不正确（应在 -180 到 180 之间）"; await OnGetAsync(); return Page(); }
            if (loc.Radius is < 0 or > 5000) { ErrorMessage = "打卡范围半径请填 0-5000 米之间"; await OnGetAsync(); return Page(); }
            if (loc.Name?.Trim().Length > 200) { ErrorMessage = "打卡地点名称不能超过 200 个字"; await OnGetAsync(); return Page(); }
        }

        var cu = HttpContext.GetCurrentUser()!;
        if (EditId != 0 && !await IsGroupWritableAsync(cu, EditId))
        { ErrorMessage = "无权编辑该考勤组"; await OnGetAsync(); return Page(); }

        // 受限管理员：勾选的审批人、跟随部门都必须在自己范围内——不能跨分公司指定审批人，
        // 也不能把自己范围外的部门拉进来跟随这个组
        if (cu.IsScoped)
        {
            var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
            var approverDeptIds = await db.Users.Where(u => ApproverUserIds.Contains(u.Id))
                .Select(u => u.DepartmentId).ToListAsync();
            if (approverDeptIds.Any(id => !id.HasValue || !visibleIds!.Contains(id.Value)))
            { ErrorMessage = "审批人必须是同一分公司范围内的人"; await OnGetAsync(); return Page(); }
            if (SelectedDeptIds.Any(id => !visibleIds!.Contains(id)))
            { ErrorMessage = "只能勾选自己管理范围内的部门"; await OnGetAsync(); return Page(); }
        }

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
                    ApprovalLevel       = approvalLevel,
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
                    g.ApprovalLevel       = approvalLevel;
                    g.UpdatedAt           = DateTime.Now;
                    SuccessMessage = $"考勤组「{GroupName}」已更新";
                }
                else
                {
                    ErrorMessage = "更新失败：找不到该考勤组";
                }
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
        catch (Exception ex)
        {
            logger.LogError(ex, "保存考勤组失败");
            ErrorMessage = "保存失败，请稍后重试";
        }

        return RedirectToPage();
    }

    /// <summary>点“启用/停用”：切换某考勤组的启停状态。</summary>
    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var cu = HttpContext.GetCurrentUser()!;
        if (!await IsGroupWritableAsync(cu, id)) return RedirectToPage();
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
            var cu = HttpContext.GetCurrentUser()!;
            if (!await IsGroupWritableAsync(cu, id))
                throw new InvalidOperationException("无权删除该考勤组");

            var g = await db.AttendanceGroups.FindAsync(id);
            if (g is null) { ErrorMessage = "该考勤组不存在"; }
            else
            {
                // 已经有过去日期的排班就不让删：删考勤组会连带删掉本组班次和全部排班（含历史），而发工资用的
                // 月度汇总表里"标准工时/夜班天数"是按排班算的——排班一没，已经发过工资的历史月份数字会追溯变化
                // 且不可恢复。有历史排班的组请用"停用"（2026-09-24 审查修复）
                var today = DateOnly.FromDateTime(DateTime.Today);
                var pastAssignments = await db.ShiftAssignments
                    .CountAsync(a => a.ShiftSchedule.AttendanceGroupId == id && a.WorkDate < today);
                if (pastAssignments > 0)
                    throw new InvalidOperationException(
                        $"该考勤组已有 {pastAssignments} 条历史排班，直接删除会连带清掉这些排班，导致已生成的历史月份工时/夜班统计发生变化且无法恢复。请改用「停用」");

                var userCount = await db.Users.CountAsync(u => u.AttendanceGroupId == id);
                var name = g.GroupName;
                db.AttendanceGroups.Remove(g);
                await db.SaveChangesAsync();
                SuccessMessage = $"考勤组「{name}」已删除" + (userCount > 0 ? $"，原有 {userCount} 名员工已解除该考勤组归属" : "");
            }
        }
        catch (InvalidOperationException ex) { ErrorMessage = "删除失败：" + ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除考勤组失败，Id={Id}", id);
            ErrorMessage = "删除失败，请稍后重试";
        }
        return RedirectToPage();
    }
}
