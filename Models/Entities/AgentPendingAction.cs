using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// AGENT 写动作的"提案"（对应数据库表 AgentPendingAction）：
/// 助手想执行任何写操作时，先落一条提案，等管理员在当前页面点"确认执行/拒绝"后才生效。
/// 安全设计：模型永远没有直接改库的通道——唯一落库入口是确认动作时由服务端按当前权限重校验后执行。
/// </summary>
[Table("AgentPendingAction")]
public class AgentPendingAction
{
    [Key] public int Id { get; set; }

    /// <summary>提案所属会话（动作卡片挂在哪个会话下）。</summary>
    public int ConversationId { get; set; }

    /// <summary>工具名（如 punch_adjust_propose），决定确认后执行哪段逻辑。</summary>
    [Required, MaxLength(60)]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>执行参数（确认执行时反序列化使用；已含解析好的目标 id，避免"提案时合法、确认时对象变了"）。</summary>
    public string ParamJson { get; set; } = "{}";

    /// <summary>给管理员看的拟执行摘要（含影响对象与时间）。</summary>
    [MaxLength(500)]
    public string SummaryText { get; set; } = string.Empty;

    public AgentActionStatus Status { get; set; } = AgentActionStatus.Pending;

    public int CreatedBy { get; set; }                    // 提案人（发起对话的管理员）
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>过期时间：超时未处理自动置为 Expired，避免陈旧提案被误点。</summary>
    public DateTime ExpiresAt { get; set; } = DateTime.Now.AddMinutes(15);

    public int? ReviewedBy { get; set; }                  // 确认人
    public DateTime? ReviewedAt { get; set; }

    /// <summary>执行结果摘要（成功时描述做了什么）。</summary>
    [MaxLength(500)]
    public string? ResultText { get; set; }

    /// <summary>执行失败原因（仅失败时填）。</summary>
    [MaxLength(500)]
    public string? ErrorText { get; set; }

    /// <summary>该动作是否可撤回（如删除员工/重置密码这类不可逆的为 false）。</summary>
    public bool Undoable { get; set; }

    /// <summary>执行前快照（JSON）：撤回时按它还原现场（不同工具存不同对象，见 AgentActionService）。</summary>
    public string? SnapshotJson { get; set; }

    /// <summary>已撤回时间（非空=已被撤回，不可再次撤回）。</summary>
    public DateTime? UndoneAt { get; set; }
    public int? UndoneBy { get; set; }

    [ForeignKey("ConversationId")]
    public AgentConversation? Conversation { get; set; }
}
