using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// FaceVerifyAttempt：按(员工,时间)建索引，匹配远程打卡限流查询"这个人最近失败了几次"
/// </summary>
public class FaceVerifyAttemptConfiguration : IEntityTypeConfiguration<FaceVerifyAttempt>
{
    public void Configure(EntityTypeBuilder<FaceVerifyAttempt> e)
    {
        e.HasIndex(a => new { a.UserId, a.CreatedAt });

        e.HasOne(a => a.User)
         .WithMany()
         .HasForeignKey(a => a.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
