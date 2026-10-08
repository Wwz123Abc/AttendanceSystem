using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// User：工号唯一、手机号建索引；部门/考勤组/上级删除时把外键置空
/// </summary>
public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> e)
    {
        e.HasIndex(u => u.EmployeeNo).IsUnique();   // 工号不能重复
        e.HasIndex(u => u.Phone);                   // 给手机号建索引，查得更快
        e.HasIndex(u => u.IdNumber);                 // 给身份证号建索引，查重时更快

        // 上级被删 → 下属的“上级”字段清空（SetNull），不连带删下属
        e.HasOne(u => u.Supervisor)
         .WithMany()
         .HasForeignKey(u => u.SupervisorUserId)
         .OnDelete(DeleteBehavior.SetNull);

        // 部门被删 → 员工的“部门”字段清空，员工本身保留
        e.HasOne(u => u.Department)
         .WithMany(d => d.Users)
         .HasForeignKey(u => u.DepartmentId)
         .OnDelete(DeleteBehavior.SetNull);

        // 考勤组被删 → 员工的“考勤组”字段清空，员工本身保留
        e.HasOne(u => u.AttendanceGroup)
         .WithMany(g => g.Users)
         .HasForeignKey(u => u.AttendanceGroupId)
         .OnDelete(DeleteBehavior.SetNull);

        // 范围限定部门用 Restrict（不允许直接删除还被某个管理员当作管理范围的部门）——
        // 如果改成 SetNull，删部门会让这个管理员"意外"变成不受限的总部超级管理员，
        // 静默扩大权限比拒绝删除危险得多，必须让操作者先手动把这个人重新指定到别的部门再删。
        e.HasOne(u => u.ScopedDepartment)
         .WithMany()
         .HasForeignKey(u => u.ScopedDepartmentId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
