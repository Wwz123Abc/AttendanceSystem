using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 助手会话里的单条消息（对应数据库表 AgentMessage）。
/// Role：user=管理员提问；assistant=模型回复；system/tool 预留（二期工具调用）。
/// 内容脱敏约定：凡发给模型/模型返回的内容都不含身份证号/完整手机号/住址等敏感字段
/// （敏感字段只在"执行动作"阶段由系统直接读库使用，见设计文档 §6）。
/// </summary>
[Table("AgentMessage")]
public class AgentMessage
{
    [Key] public int Id { get; set; }

    public int ConversationId { get; set; }

    /// <summary>消息角色：user / assistant / system / tool。</summary>
    [Required, MaxLength(20)]
    public string Role { get; set; } = "user";

    /// <summary>消息内容（提问或回复原文）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>本次调用实际用的模型 id（便于日后排查模型切换/回归）。</summary>
    [MaxLength(100)]
    public string? ModelName { get; set; }

    /// <summary>Token 用量（模型侧统计），做费用核算用。</summary>
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    /// <summary>
    /// 本轮（assistant 消息）调用过的工具轨迹，纯展示用（如"user_search(keyword=张三) → attendance_anomaly_list(start=2026-09-07)"）。
    /// 只记工具名+关键参数，不含工具返回的实际数据，避免把范围内业务数据重复落一份到这里。
    /// </summary>
    public string? ToolTraceText { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [ForeignKey("ConversationId")]
    public AgentConversation? Conversation { get; set; }
}
