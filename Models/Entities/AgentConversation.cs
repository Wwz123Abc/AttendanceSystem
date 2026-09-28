using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 管理员助手会话（对应数据库表 AgentConversation）：
/// 一位管理员开一局与 AGENT 的连续对话，消息都挂在这个会话下面。
/// 权限说明：会话只属于创建它的用户；跨用户访问在服务层拦截（查询一律带 UserId）。
/// </summary>
[Table("AgentConversation")]
public class AgentConversation
{
    [Key] public int Id { get; set; }

    /// <summary>所属管理员用户（会话的"主人"）。</summary>
    public int UserId { get; set; }

    /// <summary>会话标题：第一次提问时用提问内容前若干字自动生成，也允许留空显示"新对话"。</summary>
    [MaxLength(200)]
    public string? Title { get; set; }

    /// <summary>是否有效：删除会话是软删（保留消息做审计追溯）。</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [ForeignKey("UserId")]
    public User? User { get; set; }
}
