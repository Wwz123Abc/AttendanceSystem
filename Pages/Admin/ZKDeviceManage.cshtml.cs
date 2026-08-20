using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Pages.Admin;

/// <summary>考勤机管理页：维护熵基（ZKTeco）考勤机的序列号白名单，替代原来改 appsettings.json 的方式。</summary>
[Authorize(Policy = "ManagePolicy")]
public class ZKDeviceManageModel(AttendanceDbContext db) : PageModel
{
    public List<ZKDevice> Devices { get; set; } = [];
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    // 表单字段（新增/编辑共用）
    [BindProperty] public int     Id       { get; set; }
    [BindProperty] public string  SN       { get; set; } = string.Empty;
    [BindProperty] public string? Name     { get; set; }
    [BindProperty] public bool    IsActive { get; set; } = true;

    public async Task OnGetAsync() => await LoadAsync();

    /// <summary>新增/编辑合一：Id==0 新增，否则更新已有设备。</summary>
    public async Task<IActionResult> OnPostSaveAsync()
    {
        try
        {
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

            if (Id == 0)   // 新增
            {
                db.ZKDevices.Add(new ZKDevice
                {
                    SN        = sn,
                    Name      = name,
                    IsActive  = IsActive,
                    CreatedAt = DateTime.Now
                });
                SuccessMessage = $"设备「{sn}」已添加";
            }
            else   // 编辑
            {
                var d = await db.ZKDevices.FindAsync(Id);
                if (d is not null)
                {
                    d.SN       = sn;
                    d.Name     = name;
                    d.IsActive = IsActive;
                    SuccessMessage = $"设备「{sn}」已更新";
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
        var d = await db.ZKDevices.FindAsync(id);
        if (d != null) { db.ZKDevices.Remove(d); await db.SaveChangesAsync(); }
        SuccessMessage = "已删除";
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Devices = await db.ZKDevices.OrderByDescending(d => d.CreatedAt).ToListAsync();
    }
}
