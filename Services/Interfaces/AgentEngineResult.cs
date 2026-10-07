namespace AttendanceSystem.Services.Interfaces;

/// <summary>模型一次调用的返回结果。</summary>
public record AgentEngineResult(
    string? Content,                                  // 正常回复；为 null 表示这次没输出文字（比如只发了工具请求）
    IReadOnlyList<AgentToolCall>? ToolCalls,          // 模型请求调用的工具（可为空）
    int? PromptTokens,
    int? CompletionTokens,
    string Model);
