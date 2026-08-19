namespace AttendanceSystem.Models.Options;

/// <summary>熵基（ZKTeco）考勤机 PUSH 协议对接配置。</summary>
public class ZKDeviceOptions
{
    public const string SectionName = "ZKDevice";

    /// <summary>
    /// 允许对接的设备序列号（SN）白名单。设备请求里带的 SN 不在这个名单里就拒绝，
    /// 防止别人伪造请求往库里灌数据。留空 = 不限制（仅本地联调阶段这么用，正式上线务必配上）。
    /// </summary>
    public List<string> AllowedSerialNumbers { get; set; } = [];

    /// <summary>设备心跳间隔（秒），初始化握手时告诉设备多久发一次心跳。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 10;
}
