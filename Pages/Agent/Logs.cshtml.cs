using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Pages.Agent;

/// <summary>
/// AGENT 动作审计页：查看"提案确认/拒绝"的历史。
/// 可见范围：总部超级管理员（Role=Admin 且不受限）看全部；受限管理员只能看自己的动作记录。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class LogsModel(AttendanceDbContext db) : PageModel
{
    public List<AgentActionLogDto> Rows { get; set; } = [];
    public bool IsHq { get; set; }
    public int Shown { get; set; }

    [FromQuery] public DateOnly? From { get; set; }
    [FromQuery] public DateOnly? To { get; set; }
    [FromQuery] public int Result { get; set; }        // 0=全部 1=成功 2=失败
    [FromQuery] public string? Operator { get; set; }  // 按提案人姓名/工号模糊

    public async Task OnGetAsync()
    {
        var cu = HttpContext.GetCurrentUser()!;
        IsHq = cu.Role == UserRole.Admin && !cu.IsScoped;

        var q = db.AgentActionLogs.AsNoTracking();
        if (!IsHq)
            q = q.Where(l => l.OperatorUserId == cu.UserId);
        if (From.HasValue)
            q = q.Where(l => l.CreatedAt >= From.Value.ToDateTime(TimeOnly.MinValue));
        if (To.HasValue)
            q = q.Where(l => l.CreatedAt < To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));
        if (Result == 1) q = q.Where(l => l.Success);
        if (Result == 2) q = q.Where(l => !l.Success);

        var keyword = Operator?.Trim();
        // 若关键字是纯数字，也可直接按 OperatorUserId 精确过滤
        if (keyword is not null && int.TryParse(keyword, out var kwId))
            q = q.Where(l => l.OperatorUserId == kwId);

        var raw = await q.OrderByDescending(l => l.Id).Take(500)
            .Select(l => new
            {
                l.Id, l.OperatorUserId, l.ApproverUserId, l.ToolName, l.SummaryText,
                l.ReviewAction, l.Success, l.DetailText, l.CreatedAt
            })
            .ToListAsync();

        // 取全部相关用户显示姓名（提案人/确认人；已被删除的显示占位）
        var relatedIds = raw.Select(r => r.OperatorUserId)
            .Concat(raw.Where(r => r.ApproverUserId.HasValue).Select(r => r.ApproverUserId!.Value))
            .Distinct()
            .ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => relatedIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.RealName}（{u.EmployeeNo}）");
        string NameOf(int uid) => names.GetValueOrDefault(uid, $"用户#{uid}（已删除）");

        var rows = raw.Select(l => new AgentActionLogDto
        {
            Id               = l.Id,
            ToolNameText     = ToolName(l.ToolName),
            OperatorName     = NameOf(l.OperatorUserId),
            ApproverName     = l.ApproverUserId.HasValue ? NameOf(l.ApproverUserId.Value) : "—",
            ReviewActionText = l.ReviewAction switch
            {
                "approve" => "确认执行",
                "reject"  => "拒绝",
                "undo"    => "撤回",
                _         => l.ReviewAction
            },
            SummaryText      = l.SummaryText,
            DetailText       = l.DetailText,
            Success          = l.Success,
            CreatedAtText    = l.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
        }).ToList();

        // 提案人姓名/工号模糊过滤（上面若已按数字 id 精确过滤过就不再重复过滤）
        if (!string.IsNullOrEmpty(keyword) && !int.TryParse(keyword, out _))
            rows = rows.Where(r => r.OperatorName.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        Shown = rows.Count;
        Rows = rows;
    }

    private static string ToolName(string t) => t switch
    {
        "punch_adjust_propose"        => "补卡",
        "registration_reject_propose" => "驳回登记",
        "user_toggle_propose"         => "启用/停用",
        "user_delete_propose"         => "删除员工",
        "user_blacklist_propose"      => "拉黑/移出黑名单",
        "password_reset_propose"      => "重置密码",
        "scope_change_propose"        => "调整管理范围",
        "registration_confirm_propose" => "登记建档",
        "employee_create_propose"      => "新建员工",
        "employee_update_propose"      => "修改员工资料",
        "employee_role_propose"        => "调整角色",
        "employee_batch_toggle_propose" => "批量启停",
        "holiday_add_propose"          => "新增假期",
        "holiday_delete_propose"       => "删除假期",
        "approval_handle_propose"      => "审批处理",
        "announcement_publish_propose" => "发布公告",
        "announcement_withdraw_propose" => "撤下公告",
        "device_register_propose"      => "登记考勤机",
        "device_update_propose"        => "修改考勤机",
        _                             => t
    };
}
