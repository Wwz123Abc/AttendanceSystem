using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Pages.Admin;

/// <summary>考勤机管理页：维护熵基（ZKTeco）考勤机的序列号白名单，替代原来改 appsettings.json 的方式。</summary>
[Authorize(Policy = "ManagePolicy")]
public class ZKDeviceManageModel(AttendanceDbContext db, IDeptScopeService deptScopeService, ILogger<ZKDeviceManageModel> logger) : PageModel
{
    public List<ZKDevice>    Devices { get; set; } = [];
    public List<Department>  AssignableDepts { get; set; } = [];   // "归属部门"下拉框数据源，已按范围过滤
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }
    /// <summary>只有不受限的总部超级管理员才需要在表单里手动选"归属部门"——受限管理员的设备
    /// 归属部门由服务端自动填成他自己的管理范围，不用也不能自己选。</summary>
    public bool CurrentUserIsUnscoped => !HttpContext.GetCurrentUser()!.IsScoped;

    // 表单字段（新增/编辑共用）
    [BindProperty] public int     Id           { get; set; }
    [BindProperty] public string  SN           { get; set; } = string.Empty;
    [BindProperty] public string? Name         { get; set; }
    [BindProperty] public bool    IsActive     { get; set; } = true;
    [BindProperty] public int?    DepartmentId { get; set; }   // 设备归属部门

    public async Task OnGetAsync() => await LoadAsync();

    /// <summary>新增/编辑合一：Id==0 新增，否则更新已有设备。</summary>
    public async Task<IActionResult> OnPostSaveAsync()
    {
        try
        {
            var cu = HttpContext.GetCurrentUser()!;

            if (string.IsNullOrWhiteSpace(SN))
                throw new InvalidOperationException("请填写设备序列号（SN）");
            var sn = SN.Trim();
            if (sn.Length > 50)
                throw new InvalidOperationException("序列号不能超过 50 个字符");
            var name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
            if (name?.Length > 100)
                throw new InvalidOperationException("设备别名不能超过 100 个字符");

            var snTaken = await db.ZKDevices.AnyAsync(d => d.SN == sn && d.Id != Id);
            if (snTaken)
                throw new InvalidOperationException($"序列号 {sn} 已经被别的设备使用");

            string? renamedFromSn = null;   // 编辑时如果改了 SN，记下旧 SN，保存后把排队的命令一起改过去

            if (Id == 0)   // 新增
            {
                // 受限管理员：设备归属部门强制收敛到自己的范围（哪怕前端下拉框已经只显示自己范围内的部门，
                // 后端也不能只信前端传来的值）；不受限管理员必须给设备选一个归属部门——CanAccessDeptAsync
                // 对不受限用户不管 deptId 是不是 null 都会放行，光靠它挡不住"没选部门"，这里补一道显式校验
                var newDeptId = cu.IsScoped ? cu.ScopedDepartmentId : DepartmentId;
                if (!cu.IsScoped && newDeptId is null)
                    throw new InvalidOperationException("请选择设备归属部门");
                if (!await deptScopeService.CanAccessDeptAsync(cu, newDeptId))
                    throw new InvalidOperationException("无权将设备分配到该部门");

                db.ZKDevices.Add(new ZKDevice
                {
                    SN           = sn,
                    Name         = name,
                    IsActive     = IsActive,
                    DepartmentId = newDeptId,
                    CreatedAt    = DateTime.Now
                });
                SuccessMessage = $"设备「{sn}」已添加";
            }
            else   // 编辑
            {
                var d = await db.ZKDevices.FindAsync(Id);
                if (d is not null)
                {
                    // 防止受限管理员绕过界面直接 POST 别的分公司设备的 Id 过来编辑
                    if (!await deptScopeService.CanAccessDeptAsync(cu, d.DepartmentId))
                        throw new InvalidOperationException("无权编辑该设备");

                    // 受限管理员编辑设备时保留原有归属部门，不强制改写成自己的范围根——原来这里跟新增
                    // 共用一个 effectiveDeptId，导致受限管理员哪怕只是改个别名/启停状态，设备的归属部门
                    // 也会被静默改写成他自己的范围根部门（即使这台设备原本挂在他范围内的某个下级部门）。
                    // 只有不受限的总部管理员才能通过表单实际改动归属部门。
                    var wasActive = d.IsActive;
                    var oldSn     = d.SN;
                    if (!string.Equals(oldSn, sn, StringComparison.Ordinal)) renamedFromSn = oldSn;
                    d.SN           = sn;
                    d.Name         = name;
                    d.IsActive     = IsActive;
                    if (!cu.IsScoped) d.DepartmentId = DepartmentId;
                    SuccessMessage = $"设备「{sn}」已更新";

                    // 停用这台设备时，把它还没确认执行的旧命令一并清掉——不然万一以后同一个 SN
                    // 又被重新启用（或者序列号被挪给另一台新设备复用），这些过时的命令会被当成
                    // 新命令重新投递给它，内容可能早就不对了（比如"新增某个早就又改过资料的员工"）。
                    // ExecuteDeleteAsync 立即执行、跟下面的 SaveChangesAsync 是两次独立操作，这里
                    // 包一层事务保证要么都成功、要么都不生效。
                    // ★ 必须通过 CreateExecutionStrategy().ExecuteAsync 包一层：MySql 连接配置了失败
                    // 自动重试（Program.cs 的 EnableRetryOnFailure），这种"重试策略"不允许用户自己
                    // BeginTransactionAsync，否则一律直接抛 InvalidOperationException——
                    // ApprovalService.cs:221-225 就是同一个坑当初踩过一次的记录，这里照抄同一个写法。
                    if (wasActive && !IsActive)
                    {
                        var strategy = db.Database.CreateExecutionStrategy();
                        await strategy.ExecuteAsync(async () =>
                        {
                            await using var tx = await db.Database.BeginTransactionAsync();
                            await db.ZKDeviceCommands.Where(c => c.SN == oldSn && !c.Confirmed).ExecuteDeleteAsync();
                            await db.SaveChangesAsync();
                            await tx.CommitAsync();
                        });
                    }
                }
            }
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();
            // 改了设备 SN：按旧 SN 排队、还没确认执行的下发命令要跟着改成新 SN，不然它们变成孤儿，永远没有设备来取。
            // 放在保存设备之后做（SN 撞了唯一索引时上面就已经抛异常，不会把命令挪走）
            if (renamedFromSn is not null)
                await db.ZKDeviceCommands.Where(c => c.SN == renamedFromSn && !c.Confirmed)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.SN, sn));
        }
        // 只把自己抛出来的中文校验提示给管理员看；其余（数据库报错等）记日志 + 通用文案，不露出原始报错文本
        catch (InvalidOperationException ex) { ErrorMessage = $"保存失败：{ex.Message}"; }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存设备失败，Id={Id}，SN={SN}", Id, SN);
            ErrorMessage = "保存失败，请稍后重试";
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var cu = HttpContext.GetCurrentUser()!;
        var d = await db.ZKDevices.FindAsync(id);
        if (d != null)
        {
            if (!await deptScopeService.CanAccessDeptAsync(cu, d.DepartmentId))
            { ErrorMessage = "无权删除该设备"; await LoadAsync(); return Page(); }

            try
            {
                // 考勤机命令表跟设备表之间没有建外键关联（SN 是纯字符串关联，不是真正的外键），
                // 删除设备不会自动连带删掉它名下还没确认执行的命令，这里手动清一下，理由同上面停用的注释。
                // ExecuteDeleteAsync 是立即执行、不走 SaveChanges 的，跟下面 Remove+SaveChangesAsync
                // 是两次独立的数据库操作——包一个事务，避免中间断连导致"命令清掉了、设备却还在"这种半成品状态。
                // ★ 必须通过 CreateExecutionStrategy().ExecuteAsync 包一层，理由同上（EnableRetryOnFailure
                // 配了重试策略后不允许用户自己 BeginTransactionAsync，见 ApprovalService.cs:221-225）。
                var strategy = db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await db.ZKDeviceCommands.Where(c => c.SN == d.SN && !c.Confirmed).ExecuteDeleteAsync();
                    db.ZKDevices.Remove(d);
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                });
                SuccessMessage = "已删除";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "删除设备失败，Id={Id}", id);
                ErrorMessage = "删除失败，请稍后重试";
            }
        }
        else
        {
            ErrorMessage = "删除失败：找不到该记录";
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var cu = HttpContext.GetCurrentUser()!;
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);

        var q = db.ZKDevices.Include(d => d.Department).AsQueryable();
        if (visibleIds is not null)
            q = q.Where(d => d.DepartmentId != null && visibleIds.Contains(d.DepartmentId.Value));
        Devices = await q.OrderByDescending(d => d.CreatedAt).ToListAsync();

        // 下拉框选项默认只列启用中的部门，但如果某台设备当前归属的部门后来被停用了，这个部门就不会
        // 出现在选项里——编辑弹窗用 JS 把 select 的值设成这个部门 id 时，因为没有匹配的 option，
        // 浏览器会把它当成"没选中"，哪怕管理员这次编辑压根没碰归属部门，保存后也会被静默清空。
        // 这里把"当前有设备归属着的部门"（不管是否还启用）也并进选项，保证每台设备的编辑表单
        // 都一定能找到匹配的 option，不碰这个字段就不会被意外改动。
        var currentDeptIds = Devices.Where(d => d.DepartmentId.HasValue).Select(d => d.DepartmentId!.Value).ToHashSet();
        var deptQuery = db.Departments.Where(x => x.IsActive || currentDeptIds.Contains(x.Id));
        AssignableDepts = visibleIds is null
            ? await deptQuery.OrderBy(x => x.SortIndex).ToListAsync()
            : await deptQuery.Where(x => visibleIds.Contains(x.Id)).OrderBy(x => x.SortIndex).ToListAsync();
    }
}
