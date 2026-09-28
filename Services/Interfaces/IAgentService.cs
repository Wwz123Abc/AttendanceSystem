using AttendanceSystem.Models.DTOs;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 管理员助手（AGENT）服务：会话管理 + 对话编排。
/// 权限红线：所有方法都带 userId 并从 DB 校验会话归属，杜绝跨用户访问；
/// 本阶段（M1）只做"问答"，不接任何工具，也不允许任何写操作路径。
/// </summary>
public interface IAgentService
{
    /// <summary>某管理员的会话列表（按更新时间倒序）。</summary>
    Task<List<AgentConversationDto>> GetConversationsAsync(int userId);

    /// <summary>新建一个空会话，返回会话 id。</summary>
    Task<int> CreateConversationAsync(int userId);

    /// <summary>软删自己的会话（保留消息做审计）。</summary>
    Task<bool> DeleteConversationAsync(int userId, int conversationId);

    /// <summary>某会话的全部消息（只允许会话主人读取）。</summary>
    Task<List<AgentMessageDto>> GetMessagesAsync(int userId, int conversationId);

    /// <summary>
    /// 发一条提问并拿回复：先存提问，再带历史调模型，把回复存回同一会话。
    /// 返回模型回复原文；抛 InvalidOperationException 表示业务拦截（未启用/限流/失败）。
    /// </summary>
    Task<string> SendAsync(int userId, int conversationId, string text, CancellationToken ct);

    /// <summary>
    /// 流式发一条提问：模型输出按块通过 onDelta 回调（前端打字机），内部仍会先跑完
    /// 需要的工具轮次，最后把完整回复存回会话。返回完整回复原文。
    /// </summary>
    Task<string> SendStreamingAsync(int userId, int conversationId, string text, Func<string, Task> onDelta, CancellationToken ct);

    /// <summary>某用户今天（本地时间）累计消耗的 token 数（prompt+completion），用于预算告警展示。</summary>
    Task<int> GetTodayTokenUsageAsync(int userId);
}
