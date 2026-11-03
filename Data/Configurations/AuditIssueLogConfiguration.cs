using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// AuditIssueLog：每晚核对结果的留档。按"运行日 + 考勤日"查当晚的疑点；每周清理按考勤日删，所以单独建 WorkDate 索引。
/// 员工账号被删时留档跟着删（跟 PunchAdjustLog 一致）。
/// </summary>
public class AuditIssueLogConfiguration : IEntityTypeConfiguration<AuditIssueLog>
{
    public void Configure(EntityTypeBuilder<AuditIssueLog> e)
    {
        e.Property(x => x.EmployeeNo).HasMaxLength(50);
        e.Property(x => x.RealName).HasMaxLength(50);
        e.Property(x => x.GroupName).HasMaxLength(50);
        e.Property(x => x.IssueType).HasMaxLength(40);
        e.Property(x => x.Severity).HasMaxLength(10);
        e.Property(x => x.Detail).HasMaxLength(1000);

        e.HasIndex(x => new { x.RunDate, x.WorkDate });
        e.HasIndex(x => x.WorkDate);   // 每周清理按考勤日删

        e.HasOne<User>()
         .WithMany()
         .HasForeignKey(x => x.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
