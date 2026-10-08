using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// Announcement：发布人用 Restrict（不允许直接删除仍发布过公告的用户，保留发布历史）
/// </summary>
public class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> e)
    {
        e.HasOne(a => a.Publisher)
         .WithMany()
         .HasForeignKey(a => a.PublisherUserId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
