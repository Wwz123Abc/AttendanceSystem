using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// UserZKDevice：员工-考勤机指定分配关系，同一个人同一台设备不能重复分配；
/// 员工或设备被删，关联的分配记录一起清掉
/// </summary>
public class UserZKDeviceConfiguration : IEntityTypeConfiguration<UserZKDevice>
{
    public void Configure(EntityTypeBuilder<UserZKDevice> e)
    {
        e.HasIndex(m => new { m.UserId, m.ZKDeviceId }).IsUnique();

        e.HasOne(m => m.User)
         .WithMany(u => u.UserZKDevices)
         .HasForeignKey(m => m.UserId)
         .OnDelete(DeleteBehavior.Cascade);

        e.HasOne(m => m.ZKDevice)
         .WithMany(d => d.UserZKDevices)
         .HasForeignKey(m => m.ZKDeviceId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
