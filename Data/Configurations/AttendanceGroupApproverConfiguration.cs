using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// AttendanceGroupApprover：同一考勤组不能重复配同一个审批人；组删了，审批人名单一起删；
/// 审批人用 Restrict（不允许直接删用户，避免误删正被引用的审批人）
/// </summary>
public class AttendanceGroupApproverConfiguration : IEntityTypeConfiguration<AttendanceGroupApprover>
{
    public void Configure(EntityTypeBuilder<AttendanceGroupApprover> e)
    {
        e.HasIndex(a => new { a.AttendanceGroupId, a.UserId }).IsUnique();

        e.HasOne(a => a.AttendanceGroup)
         .WithMany(g => g.Approvers)
         .HasForeignKey(a => a.AttendanceGroupId)
         .OnDelete(DeleteBehavior.Cascade);

        e.HasOne(a => a.Approver)
         .WithMany()
         .HasForeignKey(a => a.UserId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
