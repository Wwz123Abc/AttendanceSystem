using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 下发给考勤机的命令队列（对应数据库表 ZKDeviceCommand）：设备每次心跳（/iclock/getrequest）
/// 都会来问一下"有没有要我做的事"，服务器把还没发的命令给它，标记为已发。
/// 命令格式按熵基 PUSH 协议要求拼好（如 "DATA UPDATE USERINFO PIN=..."），这里只是排队等发送。
/// </summary>
[Table("ZKDeviceCommand")]
public class ZKDeviceCommand
{
    [Key] public int Id { get; set; }

    /// <summary>目标设备的序列号（SN），心跳请求带的 SN 匹配这个字段才会发给它</summary>
    [Required, MaxLength(50)]
    public string SN { get; set; } = string.Empty;

    /// <summary>命令原文（协议要求的格式，直接原样发给设备）</summary>
    [Required, MaxLength(2000)]
    public string CommandText { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>是否已经在某次心跳里发给设备了（只代表"最近一次尝试下发过"，不代表设备真的执行成功了）</summary>
    public bool Sent { get; set; } = false;

    /// <summary>最近一次下发的时间。超过 CommandConfirmTimeoutMinutes 还没等到设备确认，会被当成"这次没送达"重新下发。</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>设备是否已经通过 /iclock/devicecmd 回执确认执行成功（Return=0）——只有这个字段为 true 才代表命令真正生效了。</summary>
    public bool Confirmed { get; set; } = false;

    public DateTime? ConfirmedAt { get; set; }
}
