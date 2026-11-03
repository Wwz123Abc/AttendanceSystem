using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// AttendanceDispute：员工考勤异议单。按"员工 + 考勤日"查历史异议，按"状态 + 提交时间"查待处理列表，所以建这两个索引。
/// 员工账号被删时异议单跟着删（跟其它明细表一致）。
/// </summary>
public class AttendanceDisputeConfiguration : IEntityTypeConfiguration<AttendanceDispute>
{
    public void Configure(EntityTypeBuilder<AttendanceDispute> e)
    {
        e.Property(x => x.Reason).HasMaxLength(500);
        e.Property(x => x.ExpectedText).HasMaxLength(200);
        e.Property(x => x.Reply).HasMaxLength(500);
        e.Property(x => x.Snapshot).HasMaxLength(500);
        e.Property(x => x.HandlerName).HasMaxLength(50);

        e.HasIndex(x => new { x.UserId, x.WorkDate });
        e.HasIndex(x => new { x.Status, x.CreatedAt });

        e.HasOne<User>()
         .WithMany()
         .HasForeignKey(x => x.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
