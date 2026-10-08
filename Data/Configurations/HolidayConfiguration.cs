using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// Holiday：按(日期,考勤组)建索引，查某天是否假期更快
/// </summary>
public class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> e)
    {
        e.HasIndex(h => new { h.HolidayDate, h.AttendanceGroupId });
    }
}
