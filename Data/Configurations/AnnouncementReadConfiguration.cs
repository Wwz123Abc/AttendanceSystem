using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// AnnouncementRead：同一条公告同一个人只会有一条已读记录；公告删了/员工删了，已读记录都跟着一起删
/// </summary>
public class AnnouncementReadConfiguration : IEntityTypeConfiguration<AnnouncementRead>
{
    public void Configure(EntityTypeBuilder<AnnouncementRead> e)
    {
        e.HasIndex(r => new { r.AnnouncementId, r.UserId }).IsUnique();

        e.HasOne(r => r.Announcement)
         .WithMany(a => a.Reads)
         .HasForeignKey(r => r.AnnouncementId)
         .OnDelete(DeleteBehavior.Cascade);

        e.HasOne(r => r.User)
         .WithMany()
         .HasForeignKey(r => r.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
