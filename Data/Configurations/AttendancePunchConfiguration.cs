using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// AttendancePunch：(员工,类型,打卡时间) 唯一索引；员工删了，打卡流水一起删
/// PunchTime 落库前已经统一截断到分钟（App/设备两个来源写入前都会做），这个唯一索引就是"同一人
/// 同类型同一分钟只能一条"的数据库级兜底：高并发下即使应用层去重失手，也会在这里被拦住抛
/// DbUpdateException，交给上层的重试逻辑处理，不会产生重复数据。
/// </summary>
public class AttendancePunchConfiguration : IEntityTypeConfiguration<AttendancePunch>
{
    public void Configure(EntityTypeBuilder<AttendancePunch> e)
    {
        e.HasIndex(p => new { p.UserId, p.PunchType, p.PunchTime }).IsUnique();

        e.HasOne(p => p.User)
         .WithMany()
         .HasForeignKey(p => p.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
