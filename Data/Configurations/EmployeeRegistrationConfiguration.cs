using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// EmployeeRegistration：按手机号/身份证号建索引，方便查重；确认后关联的正式账号如果被删，
/// 只把关联字段清空，登记记录本身保留（当作审核历史，不跟着连带删除）
/// </summary>
public class EmployeeRegistrationConfiguration : IEntityTypeConfiguration<EmployeeRegistration>
{
    public void Configure(EntityTypeBuilder<EmployeeRegistration> e)
    {
        e.HasIndex(r => r.Phone);
        e.HasIndex(r => r.IdNumber);

        e.HasOne(r => r.ConfirmedUser)
         .WithMany()
         .HasForeignKey(r => r.ConfirmedUserId)
         .OnDelete(DeleteBehavior.SetNull);

        e.HasOne(r => r.Department)
         .WithMany()
         .HasForeignKey(r => r.DepartmentId)
         .OnDelete(DeleteBehavior.SetNull);
    }
}
