using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// AttendanceRecord：同一人同一天只能有一条；员工删了，其考勤记录一起删
/// </summary>
public class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    public void Configure(EntityTypeBuilder<AttendanceRecord> e)
    {
        e.HasIndex(r => new { r.UserId, r.WorkDate }).IsUnique();   // (员工,日期) 唯一
        // 单独给 WorkDate 建个索引：上面那个复合唯一索引是"先按 UserId 再按 WorkDate"排序的，
        // 查不带 UserId、只按 WorkDate 过滤的场景（每天旷工标记、看板今日统计、月度汇总等到处都是
        // "WHERE WorkDate = 今天"）用不上它，数据量大了之后每次都是全表扫描；单列索引让这类查询走索引。
        e.HasIndex(r => r.WorkDate);

        e.HasOne(r => r.User)
         .WithMany(u => u.AttendanceRecords)
         .HasForeignKey(r => r.UserId)
         .OnDelete(DeleteBehavior.Cascade);                        // 连带删除
    }
}
