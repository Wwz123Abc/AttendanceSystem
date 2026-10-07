namespace AttendanceSystem.Services.Interfaces;

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
