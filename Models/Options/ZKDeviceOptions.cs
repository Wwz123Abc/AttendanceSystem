namespace AttendanceSystem.Models.Options;

/// <summary>熵基（ZKTeco）考勤机 PUSH 协议对接配置。</summary>
public class ZKDeviceOptions
{
    public const string SectionName = "ZKDevice";

    /// <summary>设备心跳间隔（秒），初始化握手时告诉设备多久发一次心跳。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 10;
}
