using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>待确认动作的查询与展示文字（<see cref="AgentActionService"/> 的一部分）。</summary>
public partial class AgentActionService
{
    // ── 查询 ────────────────────────────────────────────────────────────────

    public async Task<List<AgentPendingActionDto>> GetActionsAsync(int userId, int conversationId)
    {
        // 顺带把属于该用户的过期提案自动作废
        await ExpireOverdueAsync(userId, conversationId);

        var rows = await db.AgentPendingActions
            .Where(a => a.ConversationId == conversationId && a.CreatedBy == userId)
            .OrderByDescending(a => a.Id)
            .Take(20)
            .Select(a => new
            {
                a.Id, a.ToolName, a.SummaryText, a.Status, a.ErrorText, a.ResultText, a.CreatedAt,
                a.Undoable, a.UndoneAt
            })
            .ToListAsync();

        return rows.Select(a => new AgentPendingActionDto
        {
            Id            = a.Id,
            ToolNameText  = ToolDisplayName(a.ToolName),
            SummaryText   = a.SummaryText,
            StatusText    = a.Status switch
            {
                AgentActionStatus.Pending  => "待确认",
                AgentActionStatus.Approved => a.UndoneAt.HasValue ? "已撤回" : a.ErrorText != null ? "执行失败" : "已执行",
                AgentActionStatus.Rejected => "已拒绝",
                _                          => "已过期"
            },
            CreatedAtText = a.CreatedAt.ToString("MM-dd HH:mm"),
            ResultText    = a.ResultText,
            ErrorText     = a.ErrorText,
            IsPending     = a.Status == AgentActionStatus.Pending,
            HighRisk      = HighRiskTools.Contains(a.ToolName),
            Undoable      = a.Undoable,
            CanUndo       = a.Undoable && a.Status == AgentActionStatus.Approved && a.ErrorText is null && !a.UndoneAt.HasValue,
            UndoHint      = UndoHintOf(a.ToolName)
        }).ToList();
    }

    /// <summary>可撤回的动作类型；删除员工/重置密码不可逆，不在此列。</summary>
    private static readonly HashSet<string> UndoableTools =
    [
        "user_toggle_propose",
        "employee_batch_toggle_propose",
        "employee_update_propose",
        "employee_role_propose",
        "registration_reject_propose",
        "registration_confirm_propose",
        "employee_create_propose",
        "punch_adjust_propose",
        "user_blacklist_propose",
        "scope_change_propose",
        "approval_submit_on_behalf_propose"
    ];

    /// <summary>撤回会影响的提示文案（确认弹窗用）。</summary>
    private static string UndoHintOf(string toolName) => toolName switch
    {
        "user_toggle_propose"           => "撤回=把账号状态改回执行前（停用→启用 / 启用→停用）",
        "employee_batch_toggle_propose" => "撤回=把这一批账号状态全部改回执行前",
        "employee_update_propose"       => "撤回=恢复被改动的字段为执行前值",
        "employee_role_propose"         => "撤回=把角色改回执行前（如 主管→员工）",
        "registration_reject_propose"   => "撤回=把该登记恢复为待确认（若有重复登记会拒绝撤回）",
        "registration_confirm_propose"  => "撤回=删除刚建的员工并恢复该登记为待确认（其考勤/关联数据会一并删除，请谨慎）",
        "employee_create_propose"       => "撤回=删除刚建的员工（其考勤/关联数据会一并删除，请谨慎）",
        "punch_adjust_propose"          => "撤回=恢复补卡前的打卡记录（原没有记录的会删除该天记录）",
        "user_blacklist_propose"        => "撤回=恢复该员工拉黑前的状态",
        "scope_change_propose"          => "撤回=恢复执行前的管理范围",
        "approval_submit_on_behalf_propose" => "撤回=撤销这张申请单（仅在审批人还没处理、状态仍为「待审批」时可撤）",
        _                               => ""
    };

    /// <summary>高风险工具清单：删除/拉黑/重置密码/调整管理范围——这些动作在页面上要红字提醒。</summary>
    private static readonly HashSet<string> HighRiskTools =
    [
        "user_delete_propose",
        "user_blacklist_propose",
        "password_reset_propose",
        "scope_change_propose"
    ];

    /// <summary>工具名的中文显示名——待确认动作列表、动作审计页共用这一份，不要再各写一份（以前
    /// 动作审计页自己复制了一份，漏加了"代提交申请"这一项，显示成英文工具名，2026-09-29 审查发现 L4）。</summary>
    internal static string ToolDisplayName(string toolName) => toolName switch
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
        "approval_handle_propose"      => "审批处理",
        "approval_submit_on_behalf_propose" => "代提申请",
        "announcement_publish_propose" => "发布公告",
        "announcement_withdraw_propose" => "撤下公告",
        "device_register_propose"      => "登记考勤机",
        "device_update_propose"        => "修改考勤机",
        _                             => toolName
    };

    private async Task ExpireOverdueAsync(int userId, int conversationId)
    {
        await db.AgentPendingActions
            .Where(a => a.ConversationId == conversationId && a.CreatedBy == userId
                     && a.Status == AgentActionStatus.Pending && a.ExpiresAt <= clock.LocalNow())
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentActionStatus.Expired));
    }
}
