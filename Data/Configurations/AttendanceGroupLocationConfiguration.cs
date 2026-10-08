using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// AttendanceGroupLocation：考勤组删了，其打卡地点一起删
/// </summary>
public class AttendanceGroupLocationConfiguration : IEntityTypeConfiguration<AttendanceGroupLocation>
{
    public void Configure(EntityTypeBuilder<AttendanceGroupLocation> e)
    {
        e.HasOne(l => l.AttendanceGroup)
         .WithMany(g => g.Locations)
         .HasForeignKey(l => l.AttendanceGroupId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
