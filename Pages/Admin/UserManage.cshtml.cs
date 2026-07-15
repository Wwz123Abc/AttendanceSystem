using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;

namespace AttendanceSystem.Pages.Admin;

/// <summary>员工管理页：左侧部门树筛选 + 右侧员工表（增删改、启停、拉黑、批量、重置密码、钉钉对接、扫码登记确认）。</summary>
[Authorize(Policy = "ManagePolicy")]
public class UserManageModel(
    IUserService userService,
    IDingTalkSyncService dingTalkSyncService,
    IAttendanceGroupService groupService,
    IEmployeeRegistrationService registrationService,
    IOptions<AppSettingsOptions> appOptions,
    IWebHostEnvironment env,                    // 用来定位 wwwroot 目录存身份证照片
    AttendanceDbContext db) : PageModel
{
    public List<User>            Users       { get; set; } = [];
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

    /// <summary>待确认的扫码登记列表（"待确认"标签页用）。</summary>
    public List<EmployeeRegistrationDto> PendingRegistrations { get; set; } = [];

    /// <summary>员工"扫码登记"页面的完整访问地址（用于生成二维码、展示可复制的链接）。</summary>
    public string RegistrationUrl => $"{Request.Scheme}://{Request.Host}/Employee/SelfRegister";

    /// <summary>部门树的一个节点。</summary>
    public record DeptNode(int Id, int? ParentId, string Name, int Depth, int MemberCount, bool HasChildren, bool IsActive);

    // ── 员工表单 ──────────────────────────────────────────────────────────────
    [BindProperty] public string  EmployeeNo     { get; set; } = string.Empty;
    [BindProperty] public string  RealName       { get; set; } = string.Empty;
    [BindProperty] public string  Role           { get; set; } = "Employee";
    [BindProperty] public int?    DeptId         { get; set; }
    [BindProperty] public int?    GroupId        { get; set; }
    [BindProperty] public int?    SuperId        { get; set; }
    [BindProperty] public string? Position       { get; set; }
    [BindProperty] public string? Phone          { get; set; }
    [BindProperty] public string? IdNumber       { get; set; }
    [BindProperty] public string? ContractCompany { get; set; }
    [BindProperty] public string? HireDate       { get; set; }
    [BindProperty] public string? HomeAddress            { get; set; }
    [BindProperty] public string? EmergencyContactName   { get; set; }
    [BindProperty] public string? EmergencyContactPhone  { get; set; }
    [BindProperty] public IFormFile? IdCardPhoto         { get; set; }   // 身份证照片，不选就是不改
    [BindProperty] public int     EditUserId     { get; set; }

    /// <summary>本次"新建员工"是不是在确认某条扫码登记（非空=确认通过后要联动把那条登记标记为已确认）。</summary>
    [BindProperty] public int? RegistrationId { get; set; }
    // 驳回登记时填的原因
    [BindProperty] public string? RejectReason { get; set; }

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
        PendingRegistrations = await registrationService.GetPendingAsync();
    }

    /// <summary>生成"扫码登记"链接的二维码图片（PNG），供"待确认"标签页里展示/打印。</summary>
    public IActionResult OnGetQr()
    {
        using var generator = new QRCodeGenerator();
        using var data      = generator.CreateQrCode(RegistrationUrl, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(10);
        return File(png, "image/png");
    }

    /// <summary>
    /// "新增员工"弹窗里选定部门后，前端调这个接口拿自动生成的工号（AJAX）。
    /// 该部门不属于已知的几个公司时返回 employeeNo=null，前端保持工号栏为空，改回手动填写。
    /// </summary>
    public async Task<JsonResult> OnGetGenerateEmployeeNoAsync(int deptId)
        => new(new { employeeNo = await userService.GenerateNextEmployeeNoAsync(deptId) });

    /// <summary>
    /// "新增/编辑员工"弹窗里选定部门后，前端调这个接口按"该部门下角色=主管的在职员工"自动带出直属上级（AJAX）。
    /// 唯一匹配到 1 人才会返回 supervisorId 让前端自动预选；匹配到 0 人或多人时 supervisorId 为空，
    /// 由前端提示管理员手动选择（count 告诉前端具体是哪种情况，用来显示不同的提示文案）。
    /// </summary>
    public async Task<JsonResult> OnGetSuggestSupervisorAsync(int deptId)
    {
        var supervisors = await db.Users
            .Where(u => u.IsActive && u.DepartmentId == deptId && u.Role == UserRole.Supervisor)
            .Select(u => new { u.Id, u.RealName })
            .ToListAsync();
        return new(new
        {
            supervisorId = supervisors.Count == 1 ? supervisors[0].Id : (int?)null,
            count        = supervisors.Count
        });
    }

    /// <summary>驳回一条扫码登记（不建账号）。</summary>
    public async Task<IActionResult> OnPostRejectRegistrationAsync(int id)
    {
        try { await registrationService.RejectAsync(id, RejectReason); SuccessMessage = "已驳回该登记"; }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync(); return Page();
    }

    // ── 增 / 改 ───────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostCreateAsync()
    {
        try
        {
            ValidateContact(requirePhone: true, requireSupervisor: true);
            var newUser = BuildUser();
            // 如果是在"确认录入"某条扫码登记，员工自己提交时可能已经上传过身份证照片；
            // 管理员这次没有重新上传的话，就沿用登记里那张，避免让员工再扫一次码补传
            string? regPhotoUrl = null;
            if (RegistrationId.HasValue)
                regPhotoUrl = (await db.EmployeeRegistrations.FindAsync(RegistrationId.Value))?.IdCardPhotoUrl;
            newUser.IdCardPhotoUrl = await SaveIdCardPhotoAsync(newUser.EmployeeNo, regPhotoUrl);
            // 部门长期跟随了某个考勤组时，自动归入该组；部门没配跟随关系则维持表单里手动选的考勤组
            if (DeptId.HasValue)
            {
                var followedGroupId = await groupService.GetGroupIdForDepartmentAsync(DeptId.Value);
                if (followedGroupId.HasValue) newUser.AttendanceGroupId = followedGroupId.Value;
            }
            var initialPwd = appOptions.Value.DefaultPassword;   // 初始密码统一取配置值（默认 123456）
            var (_, warning) = await userService.CreateUserAsync(newUser, initialPwd);

            // 如果这次新建是在确认某条扫码登记，顺带把那条登记标记为「已确认」，关联上新建好的账号
            if (RegistrationId.HasValue)
                await registrationService.MarkConfirmedAsync(RegistrationId.Value, newUser.Id);

            SuccessMessage = $"员工 {RealName} 创建成功！初始密码：{initialPwd}" + (warning is null ? "" : $"（{warning}）");
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await ReloadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync()
    {
        try
        {
            ValidateContact(requirePhone: false, requireSupervisor: false);
            var oldPhotoUrl = (await userService.GetUserByIdAsync(EditUserId))?.IdCardPhotoUrl;
            var user = BuildUser();
            user.Id = EditUserId;
            user.IdCardPhotoUrl = await SaveIdCardPhotoAsync(user.EmployeeNo, oldPhotoUrl);
            // 部门长期跟随了某个考勤组时，自动归入该组；部门没配跟随关系则维持表单里手动选的考勤组
            if (DeptId.HasValue)
            {
                var followedGroupId = await groupService.GetGroupIdForDepartmentAsync(DeptId.Value);
                if (followedGroupId.HasValue) user.AttendanceGroupId = followedGroupId.Value;
            }
            var (_, warning) = await userService.UpdateUserAsync(user);
            SuccessMessage = warning is null ? "员工信息更新成功！" : $"员工信息更新成功！（{warning}）";
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
        try
        {
            var (_, warning) = await userService.DeleteUserAsync(id);
            SuccessMessage = warning is null ? "已彻底删除该员工" : $"已彻底删除该员工（{warning}）";
        }
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
    /// 校验整张表单：工号/姓名必填且不超长、角色/入职日期格式正确、手机号/紧急联系人电话/身份证号格式正确、
    /// 岗位/合同公司/住址/紧急联系人姓名不超长。任何一项不合格都会抛异常，页面会把异常消息当提示显示出来。
    /// <paramref name="requirePhone"/>=true 时手机号还不能为空——新建员工要求必填手机号，
    /// 保证以后每个员工都能用"忘记密码"（工号+手机号+钉钉验证码）自助找回；
    /// 编辑老员工时不强制补填，避免历史上没留手机号的员工卡在其它字段也改不了。
    /// <paramref name="requireSupervisor"/>=true 时"直属上级"还不能为空——新建员工要求必选直属上级，
    /// 保证审批流程（尤其是二级审批）总能找到人；编辑老员工时同样不强制补填，避免历史遗留数据卡住其它字段的修改。
    /// </summary>
    private void ValidateContact(bool requirePhone, bool requireSupervisor)
    {
        if (string.IsNullOrWhiteSpace(EmployeeNo))
            throw new InvalidOperationException("请填写工号");
        if (EmployeeNo.Trim().Length > 50)
            throw new InvalidOperationException("工号不能超过 50 个字");
        if (string.IsNullOrWhiteSpace(RealName))
            throw new InvalidOperationException("请填写姓名");
        if (RealName.Trim().Length > 50)
            throw new InvalidOperationException("姓名不能超过 50 个字");
        if (!Enum.TryParse<UserRole>(Role, out _))
            throw new InvalidOperationException("请选择正确的角色");
        if (!string.IsNullOrEmpty(HireDate) && !DateOnly.TryParse(HireDate, out _))
            throw new InvalidOperationException("入职日期格式不正确");
        if (requireSupervisor && !SuperId.HasValue)
            throw new InvalidOperationException("请选择直属上级");

        if (requirePhone && string.IsNullOrWhiteSpace(Phone))
            throw new InvalidOperationException("请填写手机号（用于以后自助找回密码）");
        if (!string.IsNullOrWhiteSpace(Phone) &&
            !System.Text.RegularExpressions.Regex.IsMatch(Phone.Trim(), @"^1[3-9]\d{9}$"))
            throw new InvalidOperationException("请输入正确格式的手机号（11 位中国大陆手机号）");
        if (!string.IsNullOrWhiteSpace(EmergencyContactPhone) &&
            !System.Text.RegularExpressions.Regex.IsMatch(EmergencyContactPhone.Trim(), @"^1[3-9]\d{9}$"))
            throw new InvalidOperationException("请输入正确格式的紧急联系人电话（11 位中国大陆手机号）");
        if (!string.IsNullOrWhiteSpace(IdNumber) &&
            !System.Text.RegularExpressions.Regex.IsMatch(IdNumber.Trim(), @"^\d{17}[\dXx]$"))
            throw new InvalidOperationException("请输入正确格式的身份证号（18 位）");

        if (!string.IsNullOrWhiteSpace(Position) && Position.Trim().Length > 100)
            throw new InvalidOperationException("岗位不能超过 100 个字");
        if (!string.IsNullOrWhiteSpace(ContractCompany) && ContractCompany.Trim().Length > 100)
            throw new InvalidOperationException("合同公司不能超过 100 个字");
        if (!string.IsNullOrWhiteSpace(HomeAddress) && HomeAddress.Trim().Length > 200)
            throw new InvalidOperationException("家庭住址不能超过 200 个字");
        if (!string.IsNullOrWhiteSpace(EmergencyContactName) && EmergencyContactName.Trim().Length > 50)
            throw new InvalidOperationException("紧急联系人姓名不能超过 50 个字");
    }

    private User BuildUser() => new()
    {
        EmployeeNo        = EmployeeNo.Trim(),
        RealName          = RealName.Trim(),
        Role              = Enum.TryParse<UserRole>(Role, out var role) ? role : UserRole.Employee,
        DepartmentId      = DeptId,
        AttendanceGroupId = GroupId,
        SupervisorUserId  = SuperId,
        Position          = string.IsNullOrWhiteSpace(Position)       ? null : Position.Trim(),
        Phone             = string.IsNullOrWhiteSpace(Phone)          ? null : Phone.Trim(),
        IdNumber          = string.IsNullOrWhiteSpace(IdNumber)       ? null : IdNumber.Trim().ToUpperInvariant(),
        ContractCompany   = string.IsNullOrWhiteSpace(ContractCompany) ? null : ContractCompany.Trim(),
        HireDate          = !string.IsNullOrEmpty(HireDate) && DateOnly.TryParse(HireDate, out var hd) ? hd : null,
        HomeAddress            = string.IsNullOrWhiteSpace(HomeAddress)           ? null : HomeAddress.Trim(),
        EmergencyContactName   = string.IsNullOrWhiteSpace(EmergencyContactName)  ? null : EmergencyContactName.Trim(),
        EmergencyContactPhone  = string.IsNullOrWhiteSpace(EmergencyContactPhone) ? null : EmergencyContactPhone.Trim()
        // DingTalkUserId 不在员工表单里维护，新建时留空，由「从钉钉导入/自动映射」填充
        // IdCardPhotoUrl 不在这里赋值，由 SaveIdCardPhotoAsync() 上传后单独设置
        // Role/HireDate 这里用 TryParse 兜底而不是再抛异常：ValidateContact() 已经校验过一遍，正常流程走不到 fallback 分支
    };

    /// <summary>
    /// 保存上传的身份证照片：没选新文件就保留原地址（编辑时常常不重新上传）；
    /// 选了新文件就存到 wwwroot/{上传目录}/idcards/{工号}/ 下，并删掉旧照片文件（避免残留占硬盘空间）。
    /// </summary>
    private async Task<string?> SaveIdCardPhotoAsync(string employeeNo, string? oldUrl)
    {
        if (IdCardPhoto is null || IdCardPhoto.Length == 0) return oldUrl;   // 没上传新照片，保留原值

        if (IdCardPhoto.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("身份证照片不能超过 10MB");
        var ext = Path.GetExtension(IdCardPhoto.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new InvalidOperationException("身份证照片只支持 jpg / png / webp 格式");

        var uploadPath = appOptions.Value.UploadPath.Trim('/', '\\');
        var webRoot    = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir        = Path.Combine(webRoot, uploadPath, "idcards", employeeNo);
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{ext}";   // 用随机名，避免重名覆盖
        var path     = Path.Combine(dir, fileName);
        await using (var fs = System.IO.File.Create(path))
            await IdCardPhoto.CopyToAsync(fs);

        // 换了新照片，把旧文件删掉，避免每次改资料都留一张占硬盘空间
        if (!string.IsNullOrEmpty(oldUrl))
        {
            var oldPath = Path.Combine(webRoot, oldUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
        }

        return $"/{uploadPath}/idcards/{employeeNo}/{fileName}";
    }

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
        PendingRegistrations = await registrationService.GetPendingAsync();
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
            foreach (var d in kids) { DeptTree.Add(new DeptNode(d.Id, d.ParentId, d.DeptName, depth, total.GetValueOrDefault(d.Id), byParent.ContainsKey(d.Id), d.IsActive)); Walk(d.Id, depth + 1); }
        }
        Walk(0, 0);

        TotalEmployees  = await db.Users.CountAsync();
        UnassignedCount = await db.Users.CountAsync(u => u.DepartmentId == null);
    }

    private async Task LoadDropdownsAsync()
    {
        Groups      = await db.AttendanceGroups.Where(g => g.IsActive).OrderBy(g => g.GroupName).ToListAsync();
        Supervisors = await db.Users.Where(u => u.IsActive && u.Role != UserRole.Employee)
                              .OrderBy(u => u.RealName).ToListAsync();
    }
}
