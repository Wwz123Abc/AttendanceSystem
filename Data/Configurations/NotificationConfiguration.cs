using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// Notification：员工删了，其通知一起删
/// </summary>
public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> e)
    {
        e.HasOne(n => n.User)
         .WithMany(u => u.Notifications)
         .HasForeignKey(n => n.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
