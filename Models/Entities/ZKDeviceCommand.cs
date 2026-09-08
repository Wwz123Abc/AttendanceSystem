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

    /// <summary>最近一次下发的时间（null=还没发过）。超过 CommandConfirmTimeoutMinutes 还没等到设备确认，
    /// 会被当成"这次没送达"重新下发——"是否发过""发过多久了"这两件事全靠这一个字段就够判断，
    /// 不需要另外一个只写不读的 Sent 布尔字段。</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>设备是否已经通过 /iclock/devicecmd 回执确认执行成功（Return=0）——只有这个字段为 true 才代表命令真正生效了。</summary>
    public bool Confirmed { get; set; } = false;

    public DateTime? ConfirmedAt { get; set; }

    /// <summary>已经尝试下发过几次（每次心跳带给设备算一次，不管设备最后有没有确认）。
    /// 超过 <see cref="Models.Options.ZKDeviceOptions.MaxSendAttempts"/> 会被标记 <see cref="Failed"/>，不再重发。</summary>
    public int SentCount { get; set; } = 0;

    /// <summary>重发次数用完了还是没等到设备确认，判定为"这条命令大概率发不出去/设备不认"，
    /// 不再排进心跳继续下发（避免无限重发占着 MaxCommandsPerHeartbeat 的名额，饿死新命令）。</summary>
    public bool Failed { get; set; } = false;
}
