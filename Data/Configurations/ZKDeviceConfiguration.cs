using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// ZKDevice：序列号唯一，不允许两条设备记录共用一个 SN；归属部门被删 → 设备退回"未归类"，
/// 不连带删设备（只有总部超级管理员还能管未归类设备，不算安全绕过）
/// </summary>
public class ZKDeviceConfiguration : IEntityTypeConfiguration<ZKDevice>
{
    public void Configure(EntityTypeBuilder<ZKDevice> e)
    {
        e.HasIndex(d => d.SN).IsUnique();

        e.HasOne(d => d.Department)
         .WithMany()
         .HasForeignKey(d => d.DepartmentId)
         .OnDelete(DeleteBehavior.SetNull);
    }
}
