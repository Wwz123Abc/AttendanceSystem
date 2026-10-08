using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// ShiftAssignment：同一人同一天只能排一个班
/// </summary>
public class ShiftAssignmentConfiguration : IEntityTypeConfiguration<ShiftAssignment>
{
    public void Configure(EntityTypeBuilder<ShiftAssignment> e)
    {
        e.HasIndex(a => new { a.UserId, a.WorkDate }).IsUnique();
    }
}
