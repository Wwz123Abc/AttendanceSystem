using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>管理员助手（AGENT）的动作日志：只追加、不建外键，审计记录独立保留。</summary>
public class AgentActionLogConfiguration : IEntityTypeConfiguration<AgentActionLog>
{
    public void Configure(EntityTypeBuilder<AgentActionLog> e)
    {
        e.HasIndex(l => l.OperatorUserId);
        e.HasIndex(l => l.CreatedAt);
    }
}
