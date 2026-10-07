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
