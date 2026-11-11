using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// ShiftAssignmentLog：排班修改留痕。按"员工+考勤日"查历史，按时间倒序翻页，所以建两个索引；
/// 员工账号被删时留痕跟着删（跟 PunchAdjustLog 一致）。
/// </summary>
public class ShiftAssignmentLogConfiguration : IEntityTypeConfiguration<ShiftAssignmentLog>
{
    public void Configure(EntityTypeBuilder<ShiftAssignmentLog> e)
    {
        e.HasIndex(l => new { l.UserId, l.WorkDate });
        e.HasIndex(l => l.CreatedAt);

        e.HasOne<User>()
         .WithMany()
         .HasForeignKey(l => l.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
