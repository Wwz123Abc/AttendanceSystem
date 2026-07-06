using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AttendanceSystem.Pages.Admin;

/// <summary>员工管理页：左侧部门树筛选 + 右侧员工表（增删改、启停、拉黑、批量、重置密码、钉钉对接）。</summary>
[Authorize(Policy = "ManagePolicy")]
public class UserManageModel(
    IUserService userService,
    IDingTalkSyncService dingTalkSyncService,
    IAttendanceGroupService groupService,
    IOptions<AppSettingsOptions> appOptions,
    AttendanceDbContext db) : PageModel
{
    public List<User>            Users       { get; set; } = [];
    public List<Department>      Departments { get; set; } = [];   // 弹窗“部门”下拉
    public List<AttendanceGroup> Groups      { get; set; } = [];
    public List<User>            Supervisors { get; set; } = [];

    // 左侧部门树（扁平化，带层级深度）
    public List<DeptNode> DeptTree        { get; set; } = [];
    public int            TotalEmployees  { get; set; }   // 全公司人数
    public int            UnassignedCount { get; set; }   // 未分配部门的人数

    public int     Total          { get; set; }
    public int     PageIndex      { get; set; } = 1;
    public string? Keyword        { get; set; }
    public int?    SelectedDeptId { get; set; }
    public bool    Unassigned     { get; set; }
    public string? StatusFilter   { get; set; }   // active / disabled / blacklisted / null(全部)
    public string? RoleFilter     { get; set; }   // Admin/Clerk/Supervisor/TeamLeader/Employee / null(全部)
    public const int PageSize = 20;

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    /// <summary>部门树的一个节点。</summary>
    public record DeptNode(int Id, int? ParentId, string Name, int Depth, int MemberCount, bool HasChildren);

    // ── 员工表单 ──────────────────────────────────────────────────────────────
    [BindProperty] public string  EmployeeNo     { get; set; } = string.Empty;
    [BindProperty] public string  RealName       { get; set; } = string.Empty;
    [BindProperty] public string  Role           { get; set; } = "Employee";
    [BindProperty] public int?    DeptId         { get; set; }
    [BindProperty] public int?    GroupId        { get; set; }
    [BindProperty] public int?    SuperId        { get; set; }
    [BindProperty] public string? Position       { get; set; }
    [BindProperty] public string? Phone          { get; set; }
    [BindProperty] public string? HireDate       { get; set; }
    [BindProperty] public int     EditUserId     { get; set; }

    // 批量操作的员工 id（逗号分隔）
    [BindProperty] public string? BatchIds { get; set; }

    // 重置密码：管理员可手动指定新密码；留空则随机生成（沿用原逻辑）
    [BindProperty] public string? ResetPasswordValue { get; set; }

    // 当前筛选上下文（随每次提交回传，操作后保持在同一筛选/页码）
    [BindProperty] public int?    CtxDeptId     { get; set; }
    [BindProperty] public bool    CtxUnassigned { get; set; }
    [BindProperty] public string? CtxStatus     { get; set; }
    [BindProperty] public string? CtxRole       { get; set; }
    [BindProperty] public string? CtxKeyword    { get; set; }
    [BindProperty] public int     CtxPage       { get; set; } = 1;

    // 钉钉打卡同步的日期范围
    [BindProperty] public string? SyncFrom { get; set; }
    [BindProperty] public string? SyncTo   { get; set; }

    // 注意：分页参数用 p（page 是 Razor Pages 保留路由键）
    public async Task OnGetAsync(string? keyword, int p = 1, int? deptId = null,
                                 bool unassigned = false, string? status = null, string? role = null)
    {
        PageIndex      = p < 1 ? 1 : p;
        Keyword        = keyword;
        SelectedDeptId = deptId;
        Unassigned     = unassigned;
        StatusFilter   = status;
        RoleFilter     = role;

        var (list, total) = await userService.GetUsersAsync(
            deptId: deptId, role: ParseRole(role), keyword: keyword, pageIndex: PageIndex, pageSize: PageSize,
            status: ParseStatus(status), unassignedOnly: unassigned);
        Users = list; Total = total;

        await LoadTreeAsync();
        await LoadDropdownsAsync();
    }

    // ── 增 / 改 ───────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostCreateAsync()
    {
        try
        {
            ValidateContact(requirePhone: true);
            var newUser = BuildUser();
            if (DeptId.HasValue) newUser.AttendanceGroupId = await groupService.EnsureDeptGroupAsync(DeptId.Value);   // 考勤组跟随部门
            var initialPwd = appOptions.Value.DefaultPassword;   // 初始密码统一取配置值（默认 123456）
            await userService.CreateUserAsync(newUser, initialPwd);
            SuccessMessage = $"员工 {RealName} 创建成功！初始密码：{initialPwd}";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync()
    {
        try
        {
            ValidateContact(requirePhone: false);
            var user = BuildUser();
            user.Id = EditUserId;
            if (DeptId.HasValue) user.AttendanceGroupId = await groupService.EnsureDeptGroupAsync(DeptId.Value);   // 考勤组跟随部门
            await userService.UpdateUserAsync(user);
            SuccessMessage = "员工信息更新成功！";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync();
        return Page();
    }

    // ── 单个：停用 / 启用 / 拉黑 / 移出黑名单 / 删除 / 重置密码 ────────────────
    public async Task<IActionResult> OnPostDeactivateAsync(int id)
    {
        try { await userService.DeactivateUserAsync(id); SuccessMessage = "已停用该账号（无法登录）"; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostActivateAsync(int id)
    {
        try { await userService.ActivateUserAsync(id); SuccessMessage = "已启用该账号"; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostBlacklistAsync(int id)
    {
        try { await userService.BlacklistUserAsync(id); SuccessMessage = "已拉黑该员工（禁止登录，工号永不再用）"; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostRemoveBlacklistAsync(int id)
    {
        try { await userService.RemoveFromBlacklistAsync(id); SuccessMessage = "已移出黑名单（当前为“已停用”，如需恢复请再点“启用”）"; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        try { await userService.DeleteUserAsync(id); SuccessMessage = "已彻底删除该员工"; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(int id)
    {
        try { var pwd = await userService.ResetPasswordAsync(id, ResetPasswordValue); SuccessMessage = $"密码已重置为：{pwd}"; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync(); return Page();
    }

    // ── 批量：启用 / 停用 ────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostBatchActivateAsync()
    {
        try { var n = await userService.SetActiveBatchAsync(ParseIds(BatchIds), true);
              SuccessMessage = $"已启用 {n} 名员工（黑名单员工已跳过）"; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostBatchDeactivateAsync()
    {
        try { var n = await userService.SetActiveBatchAsync(ParseIds(BatchIds), false);
              SuccessMessage = $"已停用 {n} 名员工"; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync(); return Page();
    }

    // ── 钉钉对接（保留原有功能）──────────────────────────────────────────────
    public async Task<IActionResult> OnPostAutoMapDingTalkAsync()
    {
        try { var r = await dingTalkSyncService.AutoMapByJobNumberAsync();
              if (r.Success) SuccessMessage = r.Message; else ErrorMessage = r.Message; }
        catch (Exception ex) { ErrorMessage = "钉钉自动映射失败：" + ex.Message; }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostImportDingTalkAsync()
    {
        try { var r = await dingTalkSyncService.ImportEmployeesAsync();
              if (r.Success) SuccessMessage = r.Message; else ErrorMessage = r.Message; }
        catch (Exception ex) { ErrorMessage = "从钉钉导入员工失败：" + ex.Message; }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostSyncDingTalkAsync()
    {
        try
        {
            var from = string.IsNullOrWhiteSpace(SyncFrom) ? DateTime.Today.AddDays(-1) : DateTime.Parse(SyncFrom).Date;
            var to   = string.IsNullOrWhiteSpace(SyncTo)   ? DateTime.Today.AddDays(1).AddSeconds(-1) : DateTime.Parse(SyncTo).Date.AddDays(1).AddSeconds(-1);
            if (to < from) { ErrorMessage = "结束日期不能早于开始日期"; await ReloadAsync(); return Page(); }
            var r = await dingTalkSyncService.SyncAsync(from, to);
            if (r.Success) SuccessMessage = r.Message; else ErrorMessage = r.Message;
        }
        catch (Exception ex) { ErrorMessage = "钉钉打卡同步失败：" + ex.Message; }
        await ReloadAsync(); return Page();
    }

    // ── 工具方法 ──────────────────────────────────────────────────────────────
    /// <summary>
    /// 校验手机号格式（11 位中国大陆手机号）。
    /// <paramref name="requirePhone"/>=true 时手机号还不能为空——新建员工要求必填手机号，
    /// 保证以后每个员工都能用"忘记密码"（工号+手机号+钉钉验证码）自助找回；
    /// 编辑老员工时不强制补填，避免历史上没留手机号的员工卡在其它字段也改不了。
    /// </summary>
    private void ValidateContact(bool requirePhone)
    {
        if (requirePhone && string.IsNullOrWhiteSpace(Phone))
            throw new InvalidOperationException("请填写手机号（用于以后自助找回密码）");
        if (!string.IsNullOrWhiteSpace(Phone) &&
            !System.Text.RegularExpressions.Regex.IsMatch(Phone.Trim(), @"^1[3-9]\d{9}$"))
            throw new InvalidOperationException("请输入正确格式的手机号（11 位中国大陆手机号）");
    }

    private User BuildUser() => new()
    {
        EmployeeNo        = EmployeeNo.Trim(),
        RealName          = RealName.Trim(),
        Role              = Enum.Parse<UserRole>(Role),
        DepartmentId      = DeptId,
        AttendanceGroupId = GroupId,
        SupervisorUserId  = SuperId,
        Position          = string.IsNullOrWhiteSpace(Position)       ? null : Position.Trim(),
        Phone             = string.IsNullOrWhiteSpace(Phone)          ? null : Phone.Trim(),
        HireDate          = string.IsNullOrEmpty(HireDate)            ? null : DateOnly.Parse(HireDate)
        // DingTalkUserId 不在员工表单里维护，新建时留空，由「从钉钉导入/自动映射」填充
    };

    private static EmployeeStatus? ParseStatus(string? s) => s switch
    {
        "active"      => EmployeeStatus.Active,
        "disabled"    => EmployeeStatus.Disabled,
        "blacklisted" => EmployeeStatus.Blacklisted,
        _             => null
    };

    private static UserRole? ParseRole(string? s) => Enum.TryParse<UserRole>(s, out var r) ? r : null;

    private static List<int> ParseIds(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var i) ? i : 0).Where(i => i > 0).Distinct().ToList();

    /// <summary>提交操作后重新加载：沿用回传的筛选上下文，保持在同一部门/状态/页码。</summary>
    private async Task ReloadAsync()
    {
        PageIndex      = CtxPage < 1 ? 1 : CtxPage;
        Keyword        = CtxKeyword;
        SelectedDeptId = CtxDeptId;
        Unassigned     = CtxUnassigned;
        StatusFilter   = CtxStatus;
        RoleFilter     = CtxRole;

        var (list, total) = await userService.GetUsersAsync(
            deptId: CtxDeptId, role: ParseRole(CtxRole), keyword: CtxKeyword, pageIndex: PageIndex, pageSize: PageSize,
            status: ParseStatus(CtxStatus), unassignedOnly: CtxUnassigned);
        Users = list; Total = total;

        await LoadTreeAsync();
        await LoadDropdownsAsync();
    }

    private async Task LoadTreeAsync()
    {
        var depts = await db.Departments.OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
        var direct = (await db.Users.Where(u => u.DepartmentId != null)
                .GroupBy(u => u.DepartmentId!.Value).Select(g => new { Id = g.Key, C = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Id, x => x.C);
        var byParent = depts.GroupBy(d => d.ParentId ?? 0).ToDictionary(g => g.Key, g => g.ToList());

        // 成员数改成“含下级”的 rollup：点击该部门看到的就是这个数字对应的那批人，和部门管理页口径一致
        var total = new Dictionary<int, int>();
        int Rollup(int deptId)
        {
            var sum = direct.GetValueOrDefault(deptId);
            if (byParent.TryGetValue(deptId, out var kids))
                foreach (var k in kids) sum += Rollup(k.Id);
            total[deptId] = sum;
            return sum;
        }
        if (byParent.TryGetValue(0, out var roots))
            foreach (var r in roots) Rollup(r.Id);

        DeptTree = [];
        void Walk(int parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var kids)) return;
            foreach (var d in kids) { DeptTree.Add(new DeptNode(d.Id, d.ParentId, d.DeptName, depth, total.GetValueOrDefault(d.Id), byParent.ContainsKey(d.Id))); Walk(d.Id, depth + 1); }
        }
        Walk(0, 0);

        TotalEmployees  = await db.Users.CountAsync();
        UnassignedCount = await db.Users.CountAsync(u => u.DepartmentId == null);
    }

    private async Task LoadDropdownsAsync()
    {
        Departments = await db.Departments.Where(d => d.IsActive)
                              .OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
        Groups      = await db.AttendanceGroups.Where(g => g.IsActive).OrderBy(g => g.GroupName).ToListAsync();
        Supervisors = await db.Users.Where(u => u.IsActive && u.Role != UserRole.Employee)
                              .OrderBy(u => u.RealName).ToListAsync();
    }
}
