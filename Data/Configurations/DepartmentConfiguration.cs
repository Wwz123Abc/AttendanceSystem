using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// Department：部门的父子关系（上级部门删除时把子部门的 ParentId 置空）；
/// 部门长期跟随的考勤组被删 → 只把这个跟随关系清空，部门本身保留
/// </summary>
public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> e)
    {
        e.HasOne(d => d.ParentDepartment)
         .WithMany(d => d.ChildDepartments)
         .HasForeignKey(d => d.ParentId)
         .OnDelete(DeleteBehavior.SetNull);

        e.HasOne(d => d.AttendanceGroup)
         .WithMany(g => g.Departments)
         .HasForeignKey(d => d.AttendanceGroupId)
         .OnDelete(DeleteBehavior.SetNull);
    }
}
