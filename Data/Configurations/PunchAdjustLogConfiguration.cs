using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// PunchAdjustLog：管理员手动补卡的操作记录（补卡前的样子，供"撤回"用）。按"员工+考勤日"查最近一次，所以建这个索引；
/// 员工账号被删时记录跟着删。
/// </summary>
public class PunchAdjustLogConfiguration : IEntityTypeConfiguration<PunchAdjustLog>
{
    public void Configure(EntityTypeBuilder<PunchAdjustLog> e)
    {
        e.HasIndex(l => new { l.UserId, l.WorkDate });

        e.HasOne<User>()
         .WithMany()
         .HasForeignKey(l => l.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
