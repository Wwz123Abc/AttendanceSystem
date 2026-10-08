using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// ApprovalRequest：单号唯一；按(申请人,状态)建索引；申请人删了，申请一起删
/// </summary>
public class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> e)
    {
        e.HasIndex(a => a.RequestNo).IsUnique();
        e.HasIndex(a => new { a.ApplicantUserId, a.ApprovalStatus });

        e.HasOne(a => a.Applicant)
         .WithMany(u => u.ApprovalRequests)
         .HasForeignKey(a => a.ApplicantUserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
