using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 熵基（ZKTeco）考勤机白名单（对应数据库表 ZKDevice）：设备发来的请求带的序列号（SN）
/// 只有在这张表里、且启用中，才会被认成"已知设备"接受数据；后台"考勤机管理"页面维护这张表，
/// 不用再改 appsettings.json 重启服务才能加新设备。
/// </summary>
[Table("ZKDevice")]
public class ZKDevice
{
    [Key] public int Id { get; set; }

    /// <summary>设备序列号（SN），全局唯一</summary>
    [Required, MaxLength(50)]
    public string SN { get; set; } = string.Empty;

    /// <summary>设备别名，方便管理员认出是哪台/哪个位置的机器</summary>
    [MaxLength(100)]
    public string? Name { get; set; }

    /// <summary>是否启用：停用后这台设备的请求会被当成未知设备拒绝</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>最近一次成功跟服务器通信（握手/心跳/上传数据）的时间，用于后台显示在线/离线</summary>
    public DateTime? LastSeenAt { get; set; }
}
