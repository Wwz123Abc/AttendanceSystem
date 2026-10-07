namespace AttendanceSystem.Models.Enums;

/// <summary>打卡类型：这次打卡是上班、下班，还是午间必打卡。</summary>
public enum PunchType
{
    ClockIn  = 1,  // 上班打卡
    ClockOut = 2,  // 下班打卡
    MidCheck = 3   // 午间打卡（本地打卡页专用；设备同步的打卡按时间是否落在窗口内判定，不依赖这个类型）
}
