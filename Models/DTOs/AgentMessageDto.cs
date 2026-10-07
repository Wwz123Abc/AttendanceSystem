namespace AttendanceSystem.Models.DTOs;

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
