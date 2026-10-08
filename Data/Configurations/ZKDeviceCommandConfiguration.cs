using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// ZKDeviceCommand：按(SN,是否已确认)建索引，匹配心跳时"这台设备还有哪些命令没确认"的查询
/// </summary>
public class ZKDeviceCommandConfiguration : IEntityTypeConfiguration<ZKDeviceCommand>
{
    public void Configure(EntityTypeBuilder<ZKDeviceCommand> e)
    {
        e.HasIndex(c => new { c.SN, c.Confirmed });
    }
}
