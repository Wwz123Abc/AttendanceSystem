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

/// <summary>暴露给模型的工具定义（OpenAI tools 数组里的一项）。</summary>
public record AgentToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>模型请求调用某个工具。</summary>
public record AgentToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>模型一次调用的返回结果。</summary>
public record AgentEngineResult(
    string? Content,                                  // 正常回复；为 null 表示这次没输出文字（比如只发了工具请求）
    IReadOnlyList<AgentToolCall>? ToolCalls,          // 模型请求调用的工具（可为空）
    int? PromptTokens,
    int? CompletionTokens,
    string Model);

/// <summary>
/// 大模型引擎抽象：接不同的供应商（DeepSeek/通义等）只要换实现，
/// 编排逻辑（AgentService）不用改。
/// </summary>
public interface IAgentEngine
{
    /// <summary>发一轮对话。messages 已含 system 首条；tools 可空（不传则模型无工具可用）。</summary>
    Task<AgentEngineResult> CompleteAsync(
        IReadOnlyList<AgentChatMessage> messages,
        IReadOnlyList<AgentToolDefinition>? tools,
        CancellationToken ct);

    /// <summary>
    /// 流式发一轮对话：模型产出按块回调 onDelta（前端打字机效果），
    /// 结束时返回完整结果（含拼好的 tool_calls；流式下 token 统计可能为空）。
    /// </summary>
    Task<AgentEngineResult> CompleteStreamingAsync(
        IReadOnlyList<AgentChatMessage> messages,
        IReadOnlyList<AgentToolDefinition>? tools,
        Func<string, Task>? onDelta,
        CancellationToken ct);
}
