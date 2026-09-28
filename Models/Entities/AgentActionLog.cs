using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// AGENT 动作审计（对应数据库表 AgentActionLog，只追加不修改）：
/// 每次"提案确认/拒绝"都记一行，供追踪谁、何时、执行了什么（含失败）。
/// 内容同样遵守脱敏约定：只记摘要，不记身份证/完整手机号/住址等敏感字段。
/// </summary>
[Table("AgentActionLog")]
public class AgentActionLog
{
    [Key] public int Id { get; set; }

    public int ConversationId { get; set; }
    public int OperatorUserId { get; set; }   // 提案人
    public int? ApproverUserId { get; set; }  // 确认人（拒绝时也算处理人）

    [Required, MaxLength(60)]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>动作摘要（与提案 SummaryText 一致，便于追溯）。</summary>
    [MaxLength(500)]
    public string SummaryText { get; set; } = string.Empty;

    /// <summary>处理动作：approve / reject。</summary>
    [MaxLength(20)]
    public string ReviewAction { get; set; } = string.Empty;

    public bool Success { get; set; }
    public bool WasExpired { get; set; }

    [MaxLength(1000)]
    public string? DetailText { get; set; }   // 执行结果/错误/拒绝原因

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
