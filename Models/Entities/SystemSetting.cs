using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 系统设置（对应数据库表 SystemSetting）：一个设置一行（键 + 值），目前只有"公司统一饭点时段"。
/// 没有这一行就用代码里的默认值，所以不用往表里灌初始数据。
/// </summary>
[Table("SystemSetting")]
public class SystemSetting
{
    [Key, MaxLength(100)]
    public string Key { get; set; } = "";

    [Required, MaxLength(2000)]
    public string Value { get; set; } = "";

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [MaxLength(50)]
    public string? UpdatedBy { get; set; }
}
