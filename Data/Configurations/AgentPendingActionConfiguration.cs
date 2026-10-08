using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>管理员助手（AGENT）的待确认提案：会话删除时一并清理。</summary>
public class AgentPendingActionConfiguration : IEntityTypeConfiguration<AgentPendingAction>
{
    public void Configure(EntityTypeBuilder<AgentPendingAction> e)
    {
        e.HasIndex(a => a.ConversationId);
        e.HasIndex(a => a.Status);
        e.HasOne(a => a.Conversation).WithMany().HasForeignKey(a => a.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
