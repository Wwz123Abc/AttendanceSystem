using AttendanceSystem.Models.DTOs;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// AGENT 写动作的审核服务：管理员对"提案"点确认/拒绝，由这里做原子认领、
/// 二次校验并真正执行（或拒绝）。这是写操作唯一落库入口——模型永远不能直接改业务数据。
/// </summary>
public interface IAgentActionService
{
    /// <summary>某会话的动作列表（含待确认与已处理，倒序）。查询时顺带把已过期的提案自动作废。</summary>
    Task<List<AgentPendingActionDto>> GetActionsAsync(int userId, int conversationId);

    /// <summary>确认或拒绝一个动作。返回 (是否成功处理, 给用户看的消息)。</summary>
    Task<(bool ok, string message)> ReviewAsync(int userId, int actionId, bool approve);

    /// <summary>撤回一个已执行成功的动作（按执行前快照还原现场）。返回 (是否成功, 消息)。</summary>
    Task<(bool ok, string message)> UndoAsync(int userId, int actionId);
}
