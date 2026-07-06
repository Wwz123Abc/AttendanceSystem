using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 审批流程管理页：给「考勤组 + 审批类型」配置多级审批人（存成 JSON）。
/// 例如配 5,3 表示先由 5 号审、再由 3 号审。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class ApprovalFlowManageModel(AttendanceDbContext db) : PageModel
{
    public List<ApprovalFlow>    Flows     { get; set; } = [];   // 已配置的审批流
    public List<AttendanceGroup> Groups    { get; set; } = [];   // 考勤组下拉
    public List<User>            Approvers { get; set; } = [];   // 可当审批人的人（非普通员工）
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    // 表单字段
    [BindProperty] public int    EditId       { get; set; }
    [BindProperty] public int    GroupId      { get; set; }
    [BindProperty] public string ApprovalType { get; set; } = "Leave";
    [BindProperty] public string FlowName     { get; set; } = "";
    // 审批人编号，按顺序用逗号隔开，如 "5,3"
    [BindProperty] public string ApproverIds  { get; set; } = "";

    /// <summary>打开页面：加载已有审批流、考勤组、可选审批人。</summary>
    public async Task OnGetAsync()
    {
        Flows = await db.ApprovalFlows
            .Include(f => f.AttendanceGroup)
            .OrderBy(f => f.AttendanceGroup.GroupName).ThenBy(f => f.ApprovalType)
            .ToListAsync();

        Groups    = await db.AttendanceGroups.Where(g => g.IsActive).OrderBy(g => g.GroupName).ToListAsync();
        Approvers = await db.Users.Where(u => u.IsActive && u.Role != UserRole.Employee)   // 普通员工不能当审批人
                            .OrderBy(u => u.RealName).ToListAsync();
    }

    /// <summary>点“保存”：新增或修改一条审批流。</summary>
    public async Task<IActionResult> OnPostSaveAsync()
    {
        try
        {
            // 把 "5,3" 拆开，转成 [{StepOrder:1,ApproverUserId:5},{StepOrder:2,ApproverUserId:3}] 的 JSON
            var ids = ApproverIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0) throw new Exception("至少需要配置一个审批人");

            var stepsJson = JsonSerializer.Serialize(
                ids.Select((id, i) => new { StepOrder = i + 1, ApproverUserId = int.Parse(id.Trim()) }));
            var type = Enum.Parse<ApprovalType>(ApprovalType);

            if (EditId == 0)   // 新增
            {
                db.ApprovalFlows.Add(new ApprovalFlow
                {
                    AttendanceGroupId = GroupId,
                    ApprovalType      = type,
                    FlowName          = FlowName.Trim(),
                    StepsConfig       = stepsJson,
                    IsActive          = true,
                    CreatedAt         = DateTime.Now,
                    UpdatedAt         = DateTime.Now
                });
                SuccessMessage = $"流程「{FlowName}」已创建";
            }
            else   // 修改
            {
                var f = await db.ApprovalFlows.FindAsync(EditId);
                if (f is not null)
                {
                    f.AttendanceGroupId = GroupId;
                    f.ApprovalType      = type;
                    f.FlowName          = FlowName.Trim();
                    f.StepsConfig       = stepsJson;
                    f.UpdatedAt         = DateTime.Now;
                }
                SuccessMessage = $"流程「{FlowName}」已更新";
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        return RedirectToPage();
    }

    /// <summary>点“删除”：删掉一条审批流。</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var f = await db.ApprovalFlows.FindAsync(id);
        if (f is not null) { db.ApprovalFlows.Remove(f); await db.SaveChangesAsync(); SuccessMessage = "审批流程已删除"; }
        return RedirectToPage();
    }

    /// <summary>点“启用/停用”：切换审批流的启停。</summary>
    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var f = await db.ApprovalFlows.FindAsync(id);
        if (f is not null) { f.IsActive = !f.IsActive; f.UpdatedAt = DateTime.Now; await db.SaveChangesAsync(); }
        return RedirectToPage();
    }

    /// <summary>给页面用的小接口：选了某考勤组后，动态返回该组可选的审批人列表（JSON）。</summary>
    public async Task<JsonResult> OnGetGroupApproversAsync(int gid)
    {
        var list = await db.Users
            .Where(u => u.IsActive && u.AttendanceGroupId == gid && u.Role != UserRole.Employee)
            .Select(u => new { u.Id, u.RealName, Role = u.Role.ToString() })
            .ToListAsync();
        return new JsonResult(list);
    }
}
