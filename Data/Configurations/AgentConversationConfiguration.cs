using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// AgentConversation / AgentMessage / AgentPendingAction / AgentActionLog：管理员助手（AGENT）。
/// 会话与消息、提案随用户/会话级联清理；动作日志只追加、不建外键（审计独立保留）。
/// </summary>
public class AgentConversationConfiguration : IEntityTypeConfiguration<AgentConversation>
{
    public void Configure(EntityTypeBuilder<AgentConversation> e)
    {
        e.HasIndex(c => c.UserId);
        e.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
