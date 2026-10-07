namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 喂给模型的一条消息。
/// Role：system/user/assistant/tool。
/// Content：文本内容（assistant 发出工具请求时可为 null）。
/// ToolCalls：仅 assistant 回显"上一步模型要调哪些工具"时带。
/// ToolCallId：仅 tool（工具执行结果回填）时带，对应模型那次调用的 id。
/// </summary>
public record AgentChatMessage(
    string Role,
    string? Content,
    IReadOnlyList<AgentToolCall>? ToolCalls = null,
    string? ToolCallId = null);
