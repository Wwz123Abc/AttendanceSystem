using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// MonthlyAttendanceSummary：同一人同一年月只能有一条
/// </summary>
public class MonthlyAttendanceSummaryConfiguration : IEntityTypeConfiguration<MonthlyAttendanceSummary>
{
    public void Configure(EntityTypeBuilder<MonthlyAttendanceSummary> e)
    {
        e.HasIndex(s => new { s.UserId, s.Year, s.Month }).IsUnique();
    }
}
