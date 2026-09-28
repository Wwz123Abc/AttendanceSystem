namespace AttendanceSystem.Models.DTOs;

/// <summary>助手会话列表里的一行。</summary>
public class AgentConversationDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "新对话";
    public string UpdatedAtText { get; set; } = string.Empty;
    public int MessageCount { get; set; }
    public int TokenTotal { get; set; }          // 本会话累计 Token 用量（估算费用用）
}

/// <summary>会话里的一条消息（页面展示用）。</summary>
public class AgentMessageDto
{
    public int Id { get; set; }
    public string Role { get; set; } = "user";   // user / assistant
    public string Content { get; set; } = string.Empty;
    public string CreatedAtText { get; set; } = string.Empty;
    /// <summary>本轮调用过的工具轨迹（仅 assistant 消息可能有值），供页面做"查看调用了哪些工具"的折叠展示。</summary>
    public string? ToolTraceText { get; set; }
}

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

/// <summary>审计日志列表里的一行（/Agent/Logs 页面用）。</summary>
public class AgentActionLogDto
{
    public int Id { get; set; }
    public string ToolNameText { get; set; } = string.Empty;
    public string OperatorName { get; set; } = string.Empty;   // 提案人
    public string ApproverName { get; set; } = string.Empty;   // 确认人
    public string ReviewActionText { get; set; } = string.Empty; // 确认/拒绝
    public string SummaryText { get; set; } = string.Empty;
    public string? DetailText { get; set; }
    public bool Success { get; set; }
    public string CreatedAtText { get; set; } = string.Empty;
}
