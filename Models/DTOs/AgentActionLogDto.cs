namespace AttendanceSystem.Models.DTOs;

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
