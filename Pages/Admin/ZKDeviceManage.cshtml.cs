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
public class ZKDeviceManageModel(AttendanceDbContext db, IDeptScopeService deptScopeService) : PageModel
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

            // 受限管理员：设备归属部门强制收敛到自己的范围（哪怕前端下拉框已经只显示自己范围内的部门，
            // 后端也不能只信前端传来的值）；不受限管理员必须给设备选一个归属部门
            var effectiveDeptId = cu.IsScoped ? cu.ScopedDepartmentId : DepartmentId;
            if (!await deptScopeService.CanAccessDeptAsync(cu, effectiveDeptId))
                throw new InvalidOperationException("无权将设备分配到该部门");

            var snTaken = await db.ZKDevices.AnyAsync(d => d.SN == sn && d.Id != Id);
            if (snTaken)
                throw new InvalidOperationException($"序列号 {sn} 已经被别的设备使用");

            if (Id == 0)   // 新增
            {
                db.ZKDevices.Add(new ZKDevice
                {
                    SN           = sn,
                    Name         = name,
                    IsActive     = IsActive,
                    DepartmentId = effectiveDeptId,
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

                    var wasActive = d.IsActive;
                    var oldSn     = d.SN;
                    d.SN           = sn;
                    d.Name         = name;
                    d.IsActive     = IsActive;
                    d.DepartmentId = effectiveDeptId;
                    SuccessMessage = $"设备「{sn}」已更新";

                    // 停用这台设备时，把它还没确认执行的旧命令一并清掉——不然万一以后同一个 SN
                    // 又被重新启用（或者序列号被挪给另一台新设备复用），这些过时的命令会被当成
                    // 新命令重新投递给它，内容可能早就不对了（比如"新增某个早就又改过资料的员工"）。
                    if (wasActive && !IsActive)
                        await db.ZKDeviceCommands.Where(c => c.SN == oldSn && !c.Confirmed).ExecuteDeleteAsync();
                }
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { ErrorMessage = $"保存失败：{ex.Message}"; }

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

            // 考勤机命令表跟设备表之间没有建外键关联（SN 是纯字符串关联，不是真正的外键），
            // 删除设备不会自动连带删掉它名下还没确认执行的命令，这里手动清一下，理由同上面停用的注释。
            await db.ZKDeviceCommands.Where(c => c.SN == d.SN && !c.Confirmed).ExecuteDeleteAsync();
            db.ZKDevices.Remove(d);
            await db.SaveChangesAsync();
        }
        SuccessMessage = "已删除";
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

        AssignableDepts = visibleIds is null
            ? await db.Departments.Where(x => x.IsActive).OrderBy(x => x.SortIndex).ToListAsync()
            : await db.Departments.Where(x => x.IsActive && visibleIds.Contains(x.Id)).OrderBy(x => x.SortIndex).ToListAsync();
    }
}
