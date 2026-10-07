namespace AttendanceSystem.Models.DTOs;

/// <summary>待确认动作卡片（页面展示用）。</summary>
public class AgentPendingActionDto
{
    public int Id { get; set; }
    public string ToolNameText { get; set; } = string.Empty;   // 展示名（如"补卡"）
    public string SummaryText { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;     // 待确认/已执行/已拒绝/已过期
    public string CreatedAtText { get; set; } = string.Empty;
    public string? ResultText { get; set; }
    public string? ErrorText { get; set; }
    public bool IsPending { get; set; }
    public bool HighRisk { get; set; }                         // 高风险动作（删除/拉黑/重置密码/改范围）
    public bool Undoable { get; set; }                         // 是否可撤回
    public bool CanUndo { get; set; }                          // 已执行成功且未被撤回 → 可点【撤回】
    public string? UndoHint { get; set; }                      // 撤回影响提示
}
