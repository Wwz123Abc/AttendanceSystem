using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Pages.Admin;

/// <summary>假期管理页：按年维护 法定节假日 / 公司休息日 / 调班补班日。分公司管理员只能看到/管理
/// "仅对自己范围内考勤组生效"的假期；不挂考勤组的全公司通用假期只有总部管理员能新增/删除，
/// 但所有人都能看到（跟考勤组管理页"关联部门在自己范围内"的口径一致）。</summary>
[Authorize(Policy = "ManagePolicy")]
public class HolidayManageModel(AttendanceDbContext db, IDeptScopeService deptScopeService, ILogger<HolidayManageModel> logger) : PageModel
{
    public List<Holiday>         Holidays    { get; set; } = [];   // 当年的假期列表
    public List<AttendanceGroup> Groups      { get; set; } = [];   // 考勤组（用于“仅对某组生效”）
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    // 表单字段
    [BindProperty] public string  HolidayName { get; set; } = string.Empty;
    [BindProperty] public string  HolidayDate { get; set; } = string.Empty;
    [BindProperty] public string  HolidayType { get; set; } = "LegalHoliday";
    [BindProperty] public int?    GroupId     { get; set; }   // 空=全公司
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public int     Year        { get; set; } = DateTime.Today.Year;

    /// <summary>打开页面：加载某年的假期。</summary>
    public async Task OnGetAsync(int? year)
    {
        Year = year ?? DateTime.Today.Year;
        await LoadAsync();
    }

    /// <summary>点“添加”：新增一个假期。</summary>
    public async Task<IActionResult> OnPostAddAsync()
    {
        try
        {
            var cu = HttpContext.GetCurrentUser()!;
            // 受限管理员不能新增"全公司通用"的假期（不挂考勤组），只能给自己范围内的考勤组加假期——
            // 全公司通用假期只有总部管理员能动
            if (cu.IsScoped)
            {
                if (!GroupId.HasValue)
                    throw new InvalidOperationException("请选择考勤组（不能新增全公司通用假期）");
                if (!await IsGroupWritableAsync(cu, GroupId.Value))
                    throw new InvalidOperationException("无权给该考勤组设置假期");
            }

            // 先判断原始值是否为空，再 Trim：字段留空提交时模型绑定会把它转成 null，
            // 直接 Trim() 会抛空引用异常（虽然外层有 catch 兜底，但会显示成一句读不懂的技术错误）
            if (string.IsNullOrWhiteSpace(HolidayName))
                throw new InvalidOperationException("请填写假期名称");
            var name = HolidayName.Trim();
            if (name.Length > 100)
                throw new InvalidOperationException("假期名称不能超过 100 个字");
            if (!DateOnly.TryParse(HolidayDate, out var date))
                throw new InvalidOperationException("请选择正确的日期");
            if (date.Year != Year)
                throw new InvalidOperationException($"日期年份（{date.Year}）跟当前筛选的年份（{Year}）不一致，请先把年份下拉切到 {date.Year} 年再添加，不然加完在列表里看不到");
            if (!Enum.TryParse<Models.Enums.HolidayType>(HolidayType, out var type) || !Enum.IsDefined(type))
                throw new InvalidOperationException("请选择正确的假期类型");
            var desc = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
            if (desc?.Length > 500)
                throw new InvalidOperationException("备注不能超过 500 个字");

            // 同一天、同一个考勤组范围（或都是"全公司通用"）不允许重复配置——不然"是不是节假日"和
            // "算不算应出勤"这两处判断可能因为命中不同记录而互相矛盾（同一天既是法定节假日又是调班补班日）。
            var conflict = await db.Holidays.AnyAsync(h => h.HolidayDate == date && h.AttendanceGroupId == GroupId);
            if (conflict)
                throw new InvalidOperationException(GroupId.HasValue
                    ? "该考勤组这天已经配置过假期/调班，不能重复添加，请先删除原有的再重新添加"
                    : "全公司这天已经配置过假期/调班，不能重复添加，请先删除原有的再重新添加");

            var holiday = new Holiday
            {
                HolidayName       = name,
                HolidayDate       = date,
                HolidayType       = type,
                AttendanceGroupId = GroupId,
                Description       = desc,
                CreatedAt         = DateTime.Now
            };
            db.Holidays.Add(holiday);
            await db.SaveChangesAsync();
            SuccessMessage = "假期添加成功";
        }
        catch (InvalidOperationException ex) { ErrorMessage = $"添加失败：{ex.Message}"; }   // 自己抛的中文校验提示，可以直接给用户看
        catch (Exception ex)
        {
            logger.LogError(ex, "新增假期失败");
            ErrorMessage = "添加失败，请稍后重试";   // 数据库层面的原始报错不直接展示给用户（可能带表名/约束名等技术细节）
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>点“删除”：删掉一个假期。</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var cu = HttpContext.GetCurrentUser()!;
        var h = await db.Holidays.FindAsync(id);
        if (h != null)
        {
            var allowed = h.AttendanceGroupId.HasValue
                ? await IsGroupWritableAsync(cu, h.AttendanceGroupId.Value)
                : !cu.IsScoped;   // 全公司通用假期只有总部管理员能删
            if (!allowed) { ErrorMessage = "无权删除该假期"; await LoadAsync(); return Page(); }

            db.Holidays.Remove(h); await db.SaveChangesAsync();
        }
        SuccessMessage = "已删除";
        await LoadAsync();
        return Page();
    }

    /// <summary>加载当年假期 + 考勤组下拉数据（OnGet/增删后都会调）。</summary>
    private async Task LoadAsync()
    {
        var cu = HttpContext.GetCurrentUser()!;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);

        var query = db.Holidays.Include(h => h.AttendanceGroup).ThenInclude(g => g!.Departments)
            .Where(h => h.HolidayDate.Year == Year);
        var all = await query.OrderBy(h => h.HolidayDate).ToListAsync();
        Holidays = visibleIds is null
            ? all
            : all.Where(h => h.AttendanceGroupId is null
                || h.AttendanceGroup!.Departments.Count == 0
                || h.AttendanceGroup.Departments.Any(d => visibleIds.Contains(d.Id))).ToList();

        var groups = await db.AttendanceGroups.Include(g => g.Departments)
            .Where(g => g.IsActive).OrderBy(g => g.GroupName).ToListAsync();
        Groups = visibleIds is null
            ? groups
            : groups.Where(g => g.Departments.Count == 0 || g.Departments.Any(d => visibleIds.Contains(d.Id))).ToList();
    }

    /// <summary>某个考勤组是否在当前登录者的管理范围内（口径跟 GroupManage 页一致）。</summary>
    private async Task<bool> IsGroupInScopeAsync(CurrentUser cu, int groupId)
    {
        if (!cu.IsScoped) return true;
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return true;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
        return deptIds.Any(id => visibleIds!.Contains(id));
    }

    /// <summary>某个考勤组是否允许当前登录者"写"（给这个组新增/删除假期）：不受限一律可以；受限管理员
    /// 只能写"关联部门都在自己范围内"的组——完全没关联部门的全局/共享考勤组（可能有多个分公司在用）
    /// 只有总部管理员能写，避免分公司管理员改动了别的分公司也在用的共用假期配置。</summary>
    private async Task<bool> IsGroupWritableAsync(CurrentUser cu, int groupId)
    {
        if (!cu.IsScoped) return true;
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return false;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
        // 必须是"关联部门都在自己范围内"才能写（All，不是 Any）——否则跨司共用组会变成双方受限管理员都能改
        return deptIds.All(id => visibleIds!.Contains(id));
    }
}
