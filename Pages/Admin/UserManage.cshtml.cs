using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;

namespace AttendanceSystem.Pages.Admin;

/// <summary>员工管理页：左侧部门树筛选 + 右侧员工表（增删改、启停、拉黑、批量、重置密码、扫码登记确认）。</summary>
[Authorize(Policy = "ManagePolicy")]
public class UserManageModel(
    IUserService userService,
    IAttendanceGroupService groupService,
    IEmployeeRegistrationService registrationService,
    IDeptScopeService deptScopeService,
    IOptions<AppSettingsOptions> appOptions,
    IWebHostEnvironment env,                    // 用来定位 wwwroot 目录存身份证照片
    AttendanceDbContext db,
    ILogger<UserManageModel> logger) : PageModel
{
    public List<User>            Users       { get; set; } = [];
    public List<AttendanceGroup> Groups      { get; set; } = [];
    public List<User>            Supervisors { get; set; } = [];
    public List<ZKDevice>        AssignableDevices { get; set; } = [];   // 建档/编辑表单里"推送到哪些考勤机"多选框的候选列表，全公司启用中的设备都在内，不按管理范围过滤
    /// <summary>当前这一页员工，每个人已经被指定推送到哪些设备（编辑弹窗回填用，key=UserId）。</summary>
    public Dictionary<int, List<int>> UserDeviceIdsByUser { get; set; } = [];
    /// <summary>当前登录者是不是"总部超级管理员"（角色=Admin 且自己不受范围限制）——只有这种人能在
    /// 表单里看到/改动"管理范围"这个字段（把某人设成分公司管理员），也只有这种人能把别人的角色设成
    /// 管理员。不能只判断"是否受限"：一个没被设置范围、但角色只是文员的账号，IsScoped 也恒为 false，
    /// 光挡"受限"挡不住这种账号提权。</summary>
    public bool CurrentUserIsUnscoped => IsHqSuperAdmin(HttpContext.GetCurrentUser()!);

    /// <summary>是不是"总部超级管理员"：角色为 Admin，且自己没有被设置管理范围。</summary>
    private static bool IsHqSuperAdmin(CurrentUser cu) => cu.IsHqSuperAdmin;

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

    /// <summary>员工"扫码登记"页面的完整访问地址（用于生成二维码、展示可复制的链接）。每个分公司的链接
    /// 都带各自的 deptId 参数，扫出来的登记天然就知道是哪个分公司的人，不用员工自己填、也不会填错——
    /// 分公司管理员登录后在"部门"树上只能看到自己的公司节点，天然也只能生成自己公司的二维码。</summary>
    public string RegistrationUrl(int? deptId) =>
        $"{Request.Scheme}://{Request.Host}/Employee/SelfRegister" + (deptId.HasValue ? $"?deptId={deptId}" : "");

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
    [BindProperty] public bool    AllowRemotePunch { get; set; }   // 是否允许用手机定位+人脸的"远程打卡"
    [BindProperty] public bool    IsAttendanceExempt { get; set; } // 是否免考勤（不需要打卡，不自动记旷工）
    [BindProperty] public int     EditUserId     { get; set; }
    /// <summary>这个人要推送到哪几台考勤机（多选，手动勾选，不再是无条件推给全部启用中的设备）。</summary>
    [BindProperty] public List<int> DeviceIds    { get; set; } = [];
    /// <summary>范围限定部门：只有当前登录者自己是"不受限"的总部超级管理员时，这个字段才会被接受——
    /// 用来把某个管理员/文员指定成某个分公司的"分公司管理员"，或者清空让其恢复成不受限。
    /// 分公司管理员自己不能设置/修改这个字段（哪怕是给别人设），必须由总部管理员操作。</summary>
    [BindProperty] public int?    ScopedDeptId   { get; set; }

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

    // 注意：分页参数用 p（page 是 Razor Pages 保留路由键）
    public async Task OnGetAsync(string? keyword, int p = 1, int? deptId = null,
                                 bool unassigned = false, string? status = null, string? role = null)
    {
        var cu = HttpContext.GetCurrentUser()!;
        deptId = await deptScopeService.ResolveEffectiveDeptIdAsync(cu, deptId);
        // "未分配部门"视图定义就是"没有部门"，天然在任何分公司范围之外，受限管理员看不到这个视图
        if (cu.IsScoped) unassigned = false;
        // 黑名单是分公司隔离规则的明确例外：拉黑=永不录用，是全公司共享信息，A分公司拉黑的人
        // B、C分公司也要能看到，防止换个分公司/换工号重新入职——查黑名单时不按管理范围钳制
        if (status == "blacklisted") deptId = null;

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
        await LoadUserDeviceIdsAsync();
        PendingRegistrations = await registrationService.GetPendingAsync(
            await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetCurrentUser()!));
    }

    /// <summary>批量取当前这一页员工各自被指定推送到的设备 Id（编辑弹窗回填用）。</summary>
    private async Task LoadUserDeviceIdsAsync()
    {
        var userIds = Users.Select(u => u.Id).ToList();
        UserDeviceIdsByUser = (await db.UserZKDevices.Where(m => userIds.Contains(m.UserId)).ToListAsync())
            .GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.Select(m => m.ZKDeviceId).ToList());
    }

    /// <summary>生成"扫码登记"链接的二维码图片（PNG），供"待确认"标签页里展示/打印。deptId 不为空时
    /// 生成该分公司专属的二维码（校验调用者管理范围能看到这个部门，不能跨分公司生成别人的码）。</summary>
    public async Task<IActionResult> OnGetQr(int? deptId)
    {
        if (deptId.HasValue && !await deptScopeService.CanAccessDeptAsync(HttpContext.GetCurrentUser()!, deptId))
            return Forbid();

        using var generator = new QRCodeGenerator();
        using var data      = generator.CreateQrCode(RegistrationUrl(deptId), QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(10);
        return File(png, "image/png");
    }

    /// <summary>
    /// "新增员工"弹窗里选定部门后，前端调这个接口拿自动生成的工号（AJAX）。
    /// 该部门不属于已知的几个公司时返回 employeeNo=null，前端保持工号栏为空，改回手动填写。
    /// </summary>
    public async Task<JsonResult> OnGetGenerateEmployeeNoAsync(int deptId)
    {
        // 前端下拉框已经只显示范围内的部门了，但这是个直接按 deptId 查询的 AJAX 接口，后端也要校验一遍，
        // 不然受限管理员可以绕过界面直接拿别的分公司的部门 id 探测/生成工号
        if (!await deptScopeService.CanAccessDeptAsync(HttpContext.GetCurrentUser()!, deptId))
            return new(new { employeeNo = (string?)null });
        return new(new { employeeNo = await userService.GenerateNextEmployeeNoAsync(deptId) });
    }

    /// <summary>
    /// "新增/编辑员工"弹窗里选定部门后，前端调这个接口按"该部门下角色=主管的在职员工"自动带出直属上级（AJAX）。
    /// 唯一匹配到 1 人才会返回 supervisorId 让前端自动预选；匹配到 0 人或多人时 supervisorId 为空，
    /// 由前端提示管理员手动选择（count 告诉前端具体是哪种情况，用来显示不同的提示文案）。
    /// </summary>
    public async Task<JsonResult> OnGetSuggestSupervisorAsync(int deptId)
    {
        if (!await deptScopeService.CanAccessDeptAsync(HttpContext.GetCurrentUser()!, deptId))
            return new(new { supervisorId = (int?)null, count = 0 });

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
        try { await registrationService.RejectAsync(id, RejectReason, HttpContext.GetCurrentUser()!); SuccessMessage = "已驳回该登记"; }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "驳回扫码登记失败，Id={Id}", id);
            ErrorMessage = "操作失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    // ── 增 / 改 ───────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostCreateAsync()
    {
        // 只有"这次请求自己真的认领成功了"，出错时才能把认领退回——表单校验没通过、或者没抢到认领
        // （别人/上一次点击已经在处理这条登记）时，这条登记的"已确认"状态是别人的，绝不能替人家退回，
        // 否则会出现"A 正在建号、B 的重复提交失败却把 A 的认领退回待确认"，同一条登记就能被建出两个账号
        // （2026-09-24 审查修复；也正是"先提示成功、又冒出已被处理"那类重复提交的后遗症）
        var claimedByMe = false;
        string? newPhotoUrl = null;   // 这次请求新落盘的证件照，建号失败时要补偿删除，不留孤儿文件
        try
        {
            ValidateContact(requirePhone: true, requireSupervisor: true, requireDept: true);

            var newUser = BuildUser();
            await ValidateScopeForSaveAsync(newUser);

            // 如果是在"确认录入"某条扫码登记，先原子"认领"这条登记——只有第一次点击/提交能抢到，
            // 管理员手滑双击或者网络重试导致同一条登记被提交两次的话，第二次会在这里直接被挡下，
            // 不会往下走到"建员工"那一步，避免同一条登记被重复建出两个账号。
            if (RegistrationId.HasValue)
            {
                var claimed = await registrationService.ClaimForConfirmAsync(RegistrationId.Value, HttpContext.GetCurrentUser()!);
                if (!claimed) throw new InvalidOperationException("该登记不存在，或已经被处理过了");
                claimedByMe = true;
            }

            // 如果是在"确认录入"某条扫码登记，员工自己提交时可能已经上传过身份证照片；
            // 管理员这次没有重新上传的话，就沿用登记里那张，避免让员工再扫一次码补传
            string? regPhotoUrl = null;
            if (RegistrationId.HasValue)
                regPhotoUrl = (await db.EmployeeRegistrations.FindAsync(RegistrationId.Value))?.IdCardPhotoUrl;
            newUser.IdCardPhotoUrl = await SaveIdCardPhotoAsync(newUser.EmployeeNo, regPhotoUrl);
            if (newUser.IdCardPhotoUrl != regPhotoUrl) newPhotoUrl = newUser.IdCardPhotoUrl;   // 这次真的新写了一个文件
            // 部门长期跟随了某个考勤组时，自动归入该组；部门没配跟随关系则维持表单里手动选的考勤组
            if (DeptId.HasValue)
            {
                var followedGroupId = await groupService.GetGroupIdForDepartmentAsync(DeptId.Value);
                if (followedGroupId.HasValue) newUser.AttendanceGroupId = followedGroupId.Value;
            }
            // 业务确认（2026-09-03 定稿）：新建员工的初始密码统一固定为 123456，方便现场/分公司管理员
            // 口头告知新员工，不用再一个个抄随机密码。2026-09-24 业务决定：首次登录不再强制改密码。
            const string initialPwd = "123456";
            await userService.CreateUserAsync(newUser, initialPwd);
            await userService.SetUserDevicesAsync(newUser.Id, DeviceIds);
            await ApplyScopeAfterSaveAsync(newUser.Id, isCreate: true);

            // 如果这次新建是在确认某条扫码登记，顺带把那条登记标记为「已确认」，关联上新建好的账号
            if (RegistrationId.HasValue)
                await registrationService.MarkConfirmedAsync(RegistrationId.Value, newUser.Id);

            SuccessMessage = $"员工 {RealName} 创建成功！初始密码：{initialPwd}";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            await CleanupAfterFailedCreateAsync(claimedByMe, newPhotoUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "新建员工失败，EmployeeNo={EmployeeNo}", EmployeeNo);
            ErrorMessage = "保存失败，请稍后重试";
            await CleanupAfterFailedCreateAsync(claimedByMe, newPhotoUrl);
        }
        await ReloadAsync();
        return Page();
    }

    /// <summary>新建员工失败后的善后：① 这次请求自己认领了扫码登记的，把登记退回"待确认"——不然这条登记既没建成号、
    /// 状态又不再是 Pending，会从列表里消失，只能让员工重新扫码提交；② 这次新写盘的证件照删掉，不留孤儿文件。
    /// 没认领成功的请求（表单校验没过、没抢到认领）什么都不退，别人的认领不是我们的。</summary>
    private async Task CleanupAfterFailedCreateAsync(bool claimedByMe, string? newPhotoUrl)
    {
        if (claimedByMe && RegistrationId.HasValue)
            await registrationService.RevertClaimAsync(RegistrationId.Value);
        DeleteIdCardFile(newPhotoUrl);
    }

    public async Task<IActionResult> OnPostUpdateAsync()
    {
        try
        {
            ValidateContact(requirePhone: false, requireSupervisor: false, requireDept: false);
            if (!await CanAccessUserAsync(EditUserId))
                throw new InvalidOperationException("无权编辑该员工");
            await EnsureCanManageTargetAsync(EditUserId);   // 文员/分公司管理员不能编辑（含降级）总部管理员等更高权限账号

            var oldPhotoUrl = (await userService.GetUserByIdAsync(EditUserId))?.IdCardPhotoUrl;
            var user = BuildUser();
            user.Id = EditUserId;
            await ValidateScopeForSaveAsync(user, EditUserId);

            // 证件照的顺序：先写新文件（不删旧的）→ 写库 → 成功了才删旧文件；写库失败（比如工号被别人占用）
            // 就把刚写的新文件删掉。以前是先删旧照片再写库，写库失败后旧照片已经不可逆丢失，
            // 库里却还指向它（2026-09-24 审查修复）
            user.IdCardPhotoUrl = await SaveIdCardPhotoAsync(user.EmployeeNo, oldPhotoUrl);
            var wroteNewPhoto = user.IdCardPhotoUrl != oldPhotoUrl;
            // 部门长期跟随了某个考勤组时，自动归入该组；部门没配跟随关系则维持表单里手动选的考勤组
            if (DeptId.HasValue)
            {
                var followedGroupId = await groupService.GetGroupIdForDepartmentAsync(DeptId.Value);
                if (followedGroupId.HasValue) user.AttendanceGroupId = followedGroupId.Value;
            }
            bool ok;
            try { ok = await userService.UpdateUserAsync(user); }
            catch
            {
                if (wroteNewPhoto) DeleteIdCardFile(user.IdCardPhotoUrl);
                throw;
            }
            if (ok)
            {
                if (wroteNewPhoto) DeleteIdCardFile(oldPhotoUrl);
                await userService.SetUserDevicesAsync(EditUserId, DeviceIds);
                await ApplyScopeAfterSaveAsync(EditUserId, isCreate: false);
            }
            else if (wroteNewPhoto)
            {
                DeleteIdCardFile(user.IdCardPhotoUrl);
            }
            SuccessMessage = ok ? "员工信息更新成功！" : "更新失败：用户不存在";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新员工失败，EditUserId={EditUserId}", EditUserId);
            ErrorMessage = "保存失败，请稍后重试";
        }
        await ReloadAsync();
        return Page();
    }

    // ── 单个：停用 / 启用 / 拉黑 / 移出黑名单 / 删除 / 重置密码 ────────────────
    // 这几个都是"拿 id 直接对某一条记录动手"的接口——光是列表/下拉框按范围过滤还不够，用户完全可以
    // 绕开界面直接拿别的分公司的员工 id 构造请求，所以每一个都要先校验目标员工是否在自己范围内，
    // 不通过直接拒绝，不往下执行。
    public async Task<IActionResult> OnPostDeactivateAsync(int id)
    {
        try
        {
            if (!await CanAccessUserAsync(id)) throw new InvalidOperationException("无权操作该员工");
            await EnsureCanManageTargetAsync(id);
            await userService.DeactivateUserAsync(id);
            SuccessMessage = "已停用该账号（无法登录）";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "停用员工失败，Id={Id}", id);
            ErrorMessage = "操作失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostActivateAsync(int id)
    {
        try
        {
            if (!await CanAccessUserAsync(id)) throw new InvalidOperationException("无权操作该员工");
            await EnsureCanManageTargetAsync(id);
            await userService.ActivateUserAsync(id); SuccessMessage = "已启用该账号";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "启用员工失败，Id={Id}", id);
            ErrorMessage = "操作失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostBlacklistAsync(int id)
    {
        try
        {
            if (!await CanAccessUserAsync(id)) throw new InvalidOperationException("无权操作该员工");
            await EnsureCanManageTargetAsync(id);
            await userService.BlacklistUserAsync(id); SuccessMessage = "已拉黑该员工（禁止登录，工号永不再用）";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "拉黑员工失败，Id={Id}", id);
            ErrorMessage = "操作失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostRemoveBlacklistAsync(int id)
    {
        try
        {
            if (!await CanAccessUserAsync(id)) throw new InvalidOperationException("无权操作该员工");
            await EnsureCanManageTargetAsync(id);
            await userService.RemoveFromBlacklistAsync(id); SuccessMessage = "已移出黑名单（当前为“已停用”，如需恢复请再点“启用”）";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "移出黑名单失败，Id={Id}", id);
            ErrorMessage = "操作失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        try
        {
            if (!await CanAccessUserAsync(id)) throw new InvalidOperationException("无权操作该员工");
            await EnsureCanManageTargetAsync(id);
            await userService.DeleteUserAsync(id);
            SuccessMessage = "已彻底删除该员工";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除员工失败，Id={Id}", id);
            ErrorMessage = "删除失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    /// <summary>清除某员工的人脸参考照（员工录入后不能自己更换，要换由管理员清除后让员工重新录入）。</summary>
    public async Task<IActionResult> OnPostClearFacePhotoAsync(int id)
    {
        try
        {
            if (!await CanAccessUserAsync(id)) throw new InvalidOperationException("无权操作该员工");
            await EnsureCanManageTargetAsync(id);
            var target = await db.Users.FindAsync(id) ?? throw new InvalidOperationException("员工不存在");
            var oldUrl = target.FaceReferencePhotoUrl;
            if (string.IsNullOrEmpty(oldUrl)) throw new InvalidOperationException("该员工还没有录入人脸照片");
            target.FaceReferencePhotoUrl = null;
            target.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();   // 先写库、成功了再删文件：写库失败时文件还在，库里也没被清掉，不会出现"库里指着一个已删除的文件"
            PrivateFileStorage.DeleteFaceReferenceFiles(env, oldUrl, logger);
            logger.LogInformation("管理员 {OperatorNo} 清除了员工 {EmployeeNo} 的人脸参考照",
                User.FindFirstValue(ClaimTypes.Name) ?? "?", target.EmployeeNo);
            SuccessMessage = $"已清除 {target.RealName} 的人脸照片，请通知其重新到「人脸信息」页录入";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "清除人脸照片失败，Id={Id}", id);
            ErrorMessage = "操作失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(int id)
    {
        try
        {
            if (!await CanAccessUserAsync(id)) throw new InvalidOperationException("无权操作该员工");
            await EnsureCanManageTargetAsync(id);
            var pwd = await userService.ResetPasswordAsync(id, ResetPasswordValue); SuccessMessage = $"密码已重置为：{pwd}";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "重置密码失败，Id={Id}", id);
            ErrorMessage = "操作失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    // ── 批量：启用 / 停用 ────────────────────────────────────────────────────
    // 批量操作同样按范围过滤：受限管理员提交的 id 列表里如果混了别的分公司的人，直接静默剔除掉，
    // 不报错——批量操作场景下报错打断整批不如"只处理有权限的那部分"来得实用。
    public async Task<IActionResult> OnPostBatchActivateAsync()
    {
        try { var ids = await FilterAccessibleUserIdsAsync(ParseIds(BatchIds));
              var n = await userService.SetActiveBatchAsync(ids, true);
              SuccessMessage = $"已启用 {n} 名员工（黑名单员工已跳过）"; }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "批量启用员工失败");
            ErrorMessage = "操作失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    public async Task<IActionResult> OnPostBatchDeactivateAsync()
    {
        try { var ids = await FilterAccessibleUserIdsAsync(ParseIds(BatchIds));
              var n = await userService.SetActiveBatchAsync(ids, false);
              SuccessMessage = $"已停用 {n} 名员工"; }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "批量停用员工失败");
            ErrorMessage = "操作失败，请稍后重试";
        }
        await ReloadAsync(); return Page();
    }

    // ── 工具方法 ──────────────────────────────────────────────────────────────

    /// <summary>目标员工是否在当前登录者的管理范围内（按目标员工的 DepartmentId 判断）。
    /// 所有"拿 id 直接操作某条记录"的接口，第一步都要过这个检查。</summary>
    private async Task<bool> CanAccessUserAsync(int userId)
    {
        var deptId = await db.Users.Where(u => u.Id == userId).Select(u => (int?)u.DepartmentId).FirstOrDefaultAsync();
        return await deptScopeService.CanAccessDeptAsync(HttpContext.GetCurrentUser()!, deptId);
    }

    /// <summary>
    /// 角色层级校验：目标员工在我的管理范围内还不够，还得"够得着"他的角色。
    /// 只有总部超级管理员（角色 Admin 且没有范围限制）可以操作所有人；其他人——
    /// ① 不能动总部管理员（Admin 且没设范围）：他们的 DepartmentId 常常挂在某个分公司下，光看部门范围，
    ///    这个分公司的文员/分公司管理员就"管得到"他，重置密码就能接管总部账号、编辑就能把总部管理员降级；
    /// ② 文员不能动任何管理员账号；
    /// ③ 分公司管理员之间（同范围的同级）维持原来的可操作，不影响分公司日常管理。
    /// 目标不存在时不在这里报错，交给后面各自的逻辑按"不存在"处理。（2026-09-24 审查修复）
    /// </summary>
    private async Task EnsureCanManageTargetAsync(int userId)
    {
        var cu = HttpContext.GetCurrentUser()!;
        if (cu.IsHqSuperAdmin) return;
        var t = await db.Users.Where(u => u.Id == userId).Select(u => new { u.Role, u.ScopedDepartmentId }).FirstOrDefaultAsync();
        if (t is null || cu.CanManageAccount(t.Role, t.ScopedDepartmentId)) return;
        throw new InvalidOperationException(t.ScopedDepartmentId is null
            ? "无权操作总部管理员账号，请联系总部管理员"
            : "文员无权操作管理员账号");
    }

    /// <summary>批量操作场景：从传入的 id 列表里只保留当前登录者能操作的那些（管理范围内 + 角色够得着），
    /// 其余的静默剔除（不报错——批量操作里"只处理有权限的那部分"比"整批因为混了一个越权 id 就全部失败"更实用）。</summary>
    private async Task<List<int>> FilterAccessibleUserIdsAsync(List<int> userIds)
    {
        var cu = HttpContext.GetCurrentUser()!;
        if (userIds.Count == 0 || IsHqSuperAdmin(cu)) return userIds;   // 本来就没传，或者是总部超级管理员，直接放行

        var targets = await db.Users.Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DepartmentId, u.Role, u.ScopedDepartmentId }).ToListAsync();
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);   // 不受限（如不带范围的文员）时为 null
        return targets
            .Where(u => visibleIds is null || (u.DepartmentId.HasValue && visibleIds.Contains(u.DepartmentId.Value)))
            .Where(u => cu.CanManageAccount(u.Role, u.ScopedDepartmentId))   // 角色层级同上
            .Select(u => u.Id).ToList();
    }

    /// <summary>保存（新建/编辑）前的范围校验：部门、直属上级都必须在当前登录者的管理范围内；
    /// 只有不受限的总部管理员才能把角色设成管理员、或者设置/修改范围限定部门；勾选的考勤机也必须
    /// 在管理范围内——这些都不能只靠前端表单不给选项来挡，必须服务端重新校验一遍，防止绕过界面直接
    /// 提交越权的表单数据。</summary>
    /// <summary>
    /// 新建/编辑保存后，落定这个账号的"管理范围"（ScopedDepartmentId）：
    /// 总部超级管理员可以自由指定（含清空=设为不受限）；非总部超管（分公司管理员/文员）新建或编辑的账号
    /// ——角色只可能是 Clerk/Supervisor/TeamLeader（Admin 已在 ValidateScopeForSaveAsync 挡掉）——
    /// 一律强制钳到操作者自己当前的范围，不能留空、也不能让对方自己选。
    /// 这里堵的是一条真实的越权提权链：以前这一步只有总部超管会执行，分公司管理员新建的文员账号
    /// ScopedDepartmentId 会一直是 null，而 null 在 DeptScopeService 里的语义是"不受限"——
    /// 等于一个分公司账号凭空建出了一个能看/管全公司数据（含重置任意人密码）的文员账号。
    /// </summary>
    private async Task ApplyScopeAfterSaveAsync(int userId, bool isCreate)
    {
        var cu = HttpContext.GetCurrentUser()!;
        if (IsHqSuperAdmin(cu))
        {
            await userService.SetScopedDepartmentAsync(userId, ScopedDeptId);
            return;
        }
        // 编辑（不是新建）时：对方原来已经有管理范围就保持不变——不能无条件改成"我自己的范围"，
        // 不然分公司管理员编辑下级分公司的文员（范围 Y ⊂ X）会把对方范围悄悄扩大成整个 X
        // （2026-09-24 第 11 轮审查）。对方范围为空（比如刚从主管改成文员）才需要钳到我的范围，堵提权链。
        if (!isCreate)
        {
            var currentScope = await db.Users.Where(u => u.Id == userId)
                .Select(u => u.ScopedDepartmentId).FirstOrDefaultAsync();
            if (currentScope.HasValue) return;
        }
        await userService.SetScopedDepartmentAsync(userId, cu.ScopedDepartmentId);
    }

    /// <summary>某个考勤组是否在当前登录者的管理范围内可用（口径跟 ShiftManage/GroupManage 页一致）：
    /// 不受限一律可以；组没关联任何部门（全局/多分公司共用组）算"在范围内"，可以拿来给自己范围内的
    /// 员工排班；组关联了部门的话，只要有一个落在自己范围内就算数（Any，不是 All——这里是"使用"
    /// 这个组给员工排班，不是"修改"组本身的配置，标准要比 IsGroupWritableAsync 松）。</summary>
    private async Task<bool> IsGroupInScopeAsync(CurrentUser cu, int groupId)
    {
        if (!cu.IsScoped) return true;
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return true;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
        return deptIds.Any(id => visibleIds!.Contains(id));
    }

    private async Task ValidateScopeForSaveAsync(User user, int? targetUserId = null)
    {
        var cu = HttpContext.GetCurrentUser()!;

        if (user.DepartmentId.HasValue && !await deptScopeService.CanAccessDeptAsync(cu, user.DepartmentId))
            throw new InvalidOperationException("无权将员工分配到该部门");

        // 考勤组归属校验：原来这里只校验了部门/上级/角色/设备，唯独漏了考勤组——受限管理员能把自己的
        // 员工挂到任意考勤组（含别的分公司的组），套用对方的班次时间、休息日、午休/晚餐扣时和
        // 迟到早退容忍度，是一条真实的越权口子（2026-09-17 代码审查发现）
        if (user.AttendanceGroupId.HasValue && !await IsGroupInScopeAsync(cu, user.AttendanceGroupId.Value))
            throw new InvalidOperationException("无权将员工分配到该考勤组");

        if (user.SupervisorUserId.HasValue)
        {
            var superDeptId = await db.Users.Where(u => u.Id == user.SupervisorUserId.Value)
                .Select(u => (int?)u.DepartmentId).FirstOrDefaultAsync();
            if (!await deptScopeService.CanAccessDeptAsync(cu, superDeptId))
                throw new InvalidOperationException("直属上级必须是同一分公司范围内的人");
        }

        // 只有"总部超级管理员"才能把别人的角色设成管理员、或者设置/修改别人的管理范围——原来这里
        // 只判断了 cu.IsScoped，导致一个"没被设置范围、但角色是文员"的账号（cu.IsScoped 恒为 false）
        // 也能畅通无阻地把自己或别人提权成 Admin，是个越权漏洞
        if (!IsHqSuperAdmin(cu))
        {
            // 只拦"角色发生变化"：把别人提升为管理员、或把管理员改成别的角色都要总部来做。
            // 以前一律拒绝 Role==Admin，而分公司管理员本身就是 Admin+范围，编辑自己（角色没变）保存必然被拒，
            // 改不了自己的手机号/照片等资料（2026-09-24 审查修复）。谁能编辑哪个管理员账号，由 EnsureCanManageTargetAsync 管。
            var currentRole = targetUserId.HasValue
                ? await db.Users.Where(u => u.Id == targetUserId.Value).Select(u => (UserRole?)u.Role).FirstOrDefaultAsync()
                : null;
            if (user.Role == UserRole.Admin && currentRole != UserRole.Admin)
                throw new InvalidOperationException("无权将角色设置为管理员，请联系总部管理员操作");
            if (user.Role != UserRole.Admin && currentRole == UserRole.Admin)
                throw new InvalidOperationException("无权修改管理员账号的角色，请联系总部管理员操作");
            if (ScopedDeptId.HasValue)
                throw new InvalidOperationException("无权设置管理范围，请联系总部管理员操作");
        }

        // 考勤机不做管理范围校验（2026-09-21 按业务要求取消隔离，方便员工借调到其他分公司时
        // 直接推送到对方的考勤机）——只确认设备真的存在且启用，不存在的 id 会查出 null，
        // 不然会一路走到 SetUserDevicesAsync 插入 UserZKDevice 时才撞外键约束报错（500），
        // 而不是在这里给出一句看得懂的"不存在"提示
        if (DeviceIds.Count > 0)
        {
            var validDeviceCount = await db.ZKDevices.CountAsync(d => DeviceIds.Contains(d.Id) && d.IsActive);
            if (validDeviceCount != DeviceIds.Distinct().Count())
                throw new InvalidOperationException("勾选的考勤机不存在或已停用");
        }
    }

    /// <summary>
    /// 校验整张表单：工号/姓名必填且不超长、角色/入职日期格式正确、手机号/紧急联系人电话/身份证号格式正确、
    /// 岗位/合同公司/住址/紧急联系人姓名不超长。任何一项不合格都会抛异常，页面会把异常消息当提示显示出来。
    /// <paramref name="requirePhone"/>=true 时手机号还不能为空——新建员工要求必填手机号，
    /// 方便日常联系和紧急情况下的通知；编辑老员工时不强制补填，避免历史上没留手机号的员工卡在其它字段也改不了。
    /// <paramref name="requireSupervisor"/>=true 时"直属上级"还不能为空——新建员工要求必选直属上级，
    /// 保证审批流程（尤其是二级审批）总能找到人；编辑老员工时同样不强制补填，避免历史遗留数据卡住其它字段的修改。
    /// </summary>
    private void ValidateContact(bool requirePhone, bool requireSupervisor, bool requireDept)
    {
        if (string.IsNullOrWhiteSpace(EmployeeNo))
            throw new InvalidOperationException("请填写工号");
        if (EmployeeNo.Trim().Length > 50)
            throw new InvalidOperationException("工号不能超过 50 个字");
        // 工号会被直接拼进身份证照片的存储目录名（见 SaveIdCardPhotoAsync），只校验长度不够——
        // 填个 "../../xxx" 就能越出预期目录建文件夹/写文件，这里限定成字母数字下划线短横线，
        // 从根上堵掉路径穿越，不依赖调用方自己记得转义
        if (!System.Text.RegularExpressions.Regex.IsMatch(EmployeeNo.Trim(), @"^[A-Za-z0-9_-]+$"))
            throw new InvalidOperationException("工号只能包含字母、数字、下划线和短横线");
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
        if (requireDept && !DeptId.HasValue)
            throw new InvalidOperationException("请选择部门");

        if (requirePhone && string.IsNullOrWhiteSpace(Phone))
            throw new InvalidOperationException("请填写手机号（用于以后自助找回密码）");
        if (!string.IsNullOrWhiteSpace(Phone) && !ContactValidationHelper.IsValidPhone(Phone))
            throw new InvalidOperationException("请输入正确格式的手机号（11 位中国大陆手机号）");
        if (!string.IsNullOrWhiteSpace(EmergencyContactPhone) && !ContactValidationHelper.IsValidPhone(EmergencyContactPhone))
            throw new InvalidOperationException("请输入正确格式的紧急联系人电话（11 位中国大陆手机号）");
        if (!string.IsNullOrWhiteSpace(IdNumber) && !ContactValidationHelper.IsValidIdNumber(IdNumber))
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
        EmergencyContactPhone  = string.IsNullOrWhiteSpace(EmergencyContactPhone) ? null : EmergencyContactPhone.Trim(),
        AllowRemotePunch       = AllowRemotePunch,
        IsAttendanceExempt     = IsAttendanceExempt
        // IdCardPhotoUrl 不在这里赋值，由 SaveIdCardPhotoAsync() 上传后单独设置
        // Role/HireDate 这里用 TryParse 兜底而不是再抛异常：ValidateContact() 已经校验过一遍，正常流程走不到 fallback 分支
    };

    /// <summary>
    /// 保存上传的身份证照片：没选新文件就保留原地址（编辑时常常不重新上传）；
    /// 选了新文件就存到 PrivateUploads/{上传目录}/idcards/{工号}/ 下（不删旧文件，见方法末尾说明）。
    /// </summary>
    private async Task<string?> SaveIdCardPhotoAsync(string employeeNo, string? oldUrl)
    {
        if (IdCardPhoto is null || IdCardPhoto.Length == 0) return oldUrl;   // 没上传新照片，保留原值

        if (IdCardPhoto.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("身份证照片不能超过 10MB");
        var ext = Path.GetExtension(IdCardPhoto.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new InvalidOperationException("身份证照片只支持 jpg / png / webp 格式");

        // 只看扩展名挡不住"把其他类型文件改个后缀名冒充图片上传"——员工自助登记的身份证照片上传
        // 入口已经有这道文件头校验，管理员这边"新增/编辑员工"漏了，这里补上，两处共用同一个方法
        // （发现于 2026-09-21 数据核查）。
        var header = new byte[12];
        await using (var headerStream = IdCardPhoto.OpenReadStream())
            await headerStream.ReadExactlyAsync(header.AsMemory(0, (int)Math.Min(12, IdCardPhoto.Length)));
        if (!ImageValidationHelper.IsValidImageHeader(ext, header))
            throw new InvalidOperationException("身份证照片文件内容与格式不符，请重新选择图片文件");

        var uploadPath = appOptions.Value.UploadPath.Trim('/', '\\');
        var privateRoot = PrivateFileStorage.GetRoot(env);   // 身份证照片是敏感文件，存在 wwwroot 之外，见 PrivateFilesController
        var dir        = Path.Combine(privateRoot, uploadPath, "idcards", employeeNo);
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{ext}";   // 用随机名，避免重名覆盖
        var path     = Path.Combine(dir, fileName);
        await using (var fs = System.IO.File.Create(path))
            await IdCardPhoto.CopyToAsync(fs);

        // 注意：这里不删旧照片。旧文件要等调用方把新地址成功写进数据库之后才能删（见 OnPostUpdateAsync），
        // 写库失败的话还得反过来删新文件——先删旧的再写库，写库一失败旧照片就永久丢了。
        return $"/{uploadPath}/idcards/{employeeNo}/{fileName}";
    }

    /// <summary>删除一张证件照文件（只在写库成功替换掉旧照片、或写库失败要回收新文件时调用）。
    /// 文件不存在/删除失败都只记日志，不能因为清理失败影响正常流程；"扫码登记"目录下的照片不删——
    /// 那份同时还挂在登记记录上，留着只是占点硬盘，删了登记详情里就看不到证件照了。</summary>
    private void DeleteIdCardFile(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Contains("/idcards/registrations/", StringComparison.Ordinal)) return;
        try
        {
            var root = Path.GetFullPath(PrivateFileStorage.GetRoot(env));
            var path = Path.GetFullPath(Path.Combine(root, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理证件照文件失败：{Url}", url);
        }
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
        var cu = HttpContext.GetCurrentUser()!;
        var deptId = await deptScopeService.ResolveEffectiveDeptIdAsync(cu, CtxDeptId);
        var unassigned = cu.IsScoped ? false : CtxUnassigned;
        // 同 OnGetAsync：黑名单全公司共享，不按管理范围钳制
        if (CtxStatus == "blacklisted") deptId = null;

        PageIndex      = CtxPage < 1 ? 1 : CtxPage;
        Keyword        = CtxKeyword;
        SelectedDeptId = deptId;
        Unassigned     = unassigned;
        StatusFilter   = CtxStatus;
        RoleFilter     = CtxRole;

        var (list, total) = await userService.GetUsersAsync(
            deptId: deptId, role: ParseRole(CtxRole), keyword: CtxKeyword, pageIndex: PageIndex, pageSize: PageSize,
            status: ParseStatus(CtxStatus), unassignedOnly: unassigned);
        Users = list; Total = total;

        await LoadTreeAsync();
        await LoadDropdownsAsync();
        await LoadUserDeviceIdsAsync();
        PendingRegistrations = await registrationService.GetPendingAsync(
            await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetCurrentUser()!));
    }

    private async Task LoadTreeAsync()
    {
        var cu = HttpContext.GetCurrentUser()!;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);

        var depts = await db.Departments.OrderBy(d => d.SortIndex).ThenBy(d => d.DeptName).ToListAsync();
        if (visibleIds is not null)
            depts = depts.Where(d => visibleIds.Contains(d.Id)).ToList();   // 受限管理员看不到范围外的部门节点

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
        // 不受限：从"顶层（无父部门）"开始遍历，跟原来一样；受限：从自己的范围根开始遍历，
        // 树顶就是自己的分公司节点本身，看不到总部或者其它平级分公司
        var rootKey = cu.ScopedDepartmentId ?? 0;
        if (byParent.TryGetValue(rootKey, out var roots))
            foreach (var r in roots) Rollup(r.Id);
        if (cu.IsScoped) Rollup(cu.ScopedDepartmentId!.Value);   // 范围根节点自己也要算一遍 rollup（上面的循环只算它的下级）

        DeptTree = [];
        void Walk(int parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var kids)) return;
            foreach (var d in kids) { DeptTree.Add(new DeptNode(d.Id, d.ParentId, d.DeptName, depth, total.GetValueOrDefault(d.Id), byParent.ContainsKey(d.Id), d.IsActive)); Walk(d.Id, depth + 1); }
        }
        if (cu.IsScoped)
        {
            // 受限管理员：树的第一层就是自己的范围根部门本身（不是它的子部门），下面才是子部门
            var rootDept = depts.FirstOrDefault(d => d.Id == cu.ScopedDepartmentId!.Value);
            if (rootDept is not null)
            {
                DeptTree.Add(new DeptNode(rootDept.Id, rootDept.ParentId, rootDept.DeptName, 0,
                    total.GetValueOrDefault(rootDept.Id), byParent.ContainsKey(rootDept.Id), rootDept.IsActive));
                Walk(rootDept.Id, 1);
            }
        }
        else
        {
            Walk(0, 0);
        }

        if (visibleIds is not null)
        {
            TotalEmployees  = await db.Users.CountAsync(u => u.DepartmentId != null && visibleIds.Contains(u.DepartmentId.Value));
            UnassignedCount = 0;   // "未分配部门"天然在任何分公司范围之外，受限管理员看不到这个统计
        }
        else
        {
            TotalEmployees  = await db.Users.CountAsync();
            UnassignedCount = await db.Users.CountAsync(u => u.DepartmentId == null);
        }
    }

    private async Task LoadDropdownsAsync()
    {
        var cu = HttpContext.GetCurrentUser()!;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);

        // 考勤组本身没有直接的"归属部门"字段，只有"长期跟随本组的部门"这个反向集合——
        // 受限管理员只能看到"跟随部门落在自己范围内"的组，以及完全没绑定任何部门的组（当成全公司通用组，
        // 跟节假日管理里"不挂考勤组的假期全公司通用"是同一个口径）
        var allGroups = await db.AttendanceGroups.Include(g => g.Departments)
            .Where(g => g.IsActive).OrderBy(g => g.GroupName).ToListAsync();
        Groups = visibleIds is null
            ? allGroups
            : allGroups.Where(g => g.Departments.Count == 0 || g.Departments.Any(d => visibleIds.Contains(d.Id))).ToList();

        var supervisorQuery = db.Users.Where(u => u.IsActive && u.Role != UserRole.Employee);
        if (visibleIds is not null)
            supervisorQuery = supervisorQuery.Where(u => u.DepartmentId != null && visibleIds.Contains(u.DepartmentId.Value));
        Supervisors = await supervisorQuery.OrderBy(u => u.RealName).ToListAsync();

        // 考勤机不受管理范围限制：任何管理员/文员都能看到全公司所有启用中的考勤机并勾选，
        // 方便给借调到其他分公司的员工推送到对方的考勤机（2026-09-21 按业务要求取消考勤机隔离）。
        AssignableDevices = await db.ZKDevices.Where(d => d.IsActive)
            .OrderBy(d => d.Name).ThenBy(d => d.SN).ToListAsync();
    }
}
