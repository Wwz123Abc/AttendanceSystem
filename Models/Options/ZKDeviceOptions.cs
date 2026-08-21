namespace AttendanceSystem.Models.Options;

/// <summary>熵基（ZKTeco）考勤机 PUSH 协议对接配置。</summary>
public class ZKDeviceOptions
{
    public const string SectionName = "ZKDevice";

    /// <summary>设备心跳间隔（秒），初始化握手时告诉设备多久发一次心跳。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 10;

    /// <summary>命令下发后多久没等到设备确认（/iclock/devicecmd 回执 Return=0）就当作没送达，重新排队下发。</summary>
    public int CommandConfirmTimeoutMinutes { get; set; } = 5;

    /// <summary>单次心跳最多带多少条待下发命令，避免批量导入/批量停用时一次性命令太多，设备处理不过来。</summary>
    public int MaxCommandsPerHeartbeat { get; set; } = 50;
}
