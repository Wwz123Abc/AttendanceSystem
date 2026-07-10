using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Pages.Admin;

/// <summary>假期管理页：按年维护 法定节假日 / 公司休息日 / 调班补班日。</summary>
[Authorize(Policy = "ManagePolicy")]
public class HolidayManageModel(AttendanceDbContext db) : PageModel
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
            // 先判断原始值是否为空，再 Trim：字段留空提交时模型绑定会把它转成 null，
            // 直接 Trim() 会抛空引用异常（虽然外层有 catch 兜底，但会显示成一句读不懂的技术错误）
            if (string.IsNullOrWhiteSpace(HolidayName))
                throw new InvalidOperationException("请填写假期名称");
            var name = HolidayName.Trim();
            if (name.Length > 100)
                throw new InvalidOperationException("假期名称不能超过 100 个字");
            if (!DateOnly.TryParse(HolidayDate, out var date))
                throw new InvalidOperationException("请选择正确的日期");
            if (!Enum.TryParse<Models.Enums.HolidayType>(HolidayType, out var type))
                throw new InvalidOperationException("请选择正确的假期类型");
            var desc = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
            if (desc?.Length > 500)
                throw new InvalidOperationException("备注不能超过 500 个字");

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
        catch (Exception ex) { ErrorMessage = $"添加失败：{ex.Message}"; }

        await LoadAsync();
        return Page();
    }

    /// <summary>点“删除”：删掉一个假期。</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var h = await db.Holidays.FindAsync(id);
        if (h != null) { db.Holidays.Remove(h); await db.SaveChangesAsync(); }
        SuccessMessage = "已删除";
        await LoadAsync();
        return Page();
    }

    /// <summary>加载当年假期 + 考勤组下拉数据（OnGet/增删后都会调）。</summary>
    private async Task LoadAsync()
    {
        Holidays = await db.Holidays
            .Include(h => h.AttendanceGroup)
            .Where(h => h.HolidayDate.Year == Year)
            .OrderBy(h => h.HolidayDate)
            .ToListAsync();
        Groups = await db.AttendanceGroups.Where(g => g.IsActive).OrderBy(g => g.GroupName).ToListAsync();
    }
}
