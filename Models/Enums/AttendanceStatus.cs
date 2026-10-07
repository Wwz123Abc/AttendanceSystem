namespace AttendanceSystem.Models.Enums;

// 本文件集中放“和考勤有关的固定选项”。每个枚举就是一组互斥的状态/类型。
// 名字后面的数字是存进数据库的编号。

/// <summary>考勤状态：某人某天的考勤结果是哪一种。</summary>
public enum AttendanceStatus
{
    Normal      = 1,  // 正常：按时上下班
    Late        = 2,  // 迟到：上班打卡晚了
    EarlyLeave  = 3,  // 早退：下班打卡早了
    Absent      = 4,  // 旷工：该上班却整天没打卡
    // 休假：节假日 / 休息日，本就不用上班。
    // ★ 2026-09-30 下线假期功能后，已经没有任何代码会再把记录设成这个状态。
    //   这个枚举值，以及代码里多处 `AttendanceStatus.Holiday` 的判断（AttendanceService / ZKDeviceSyncService /
    //   AttendanceBackgroundService / 显示名与颜色映射）只是为了兼容"下线之前已经落库的历史记录"：让它们还能
    //   正常显示、不被误当成旷工/缺卡去统计。不要把它当成"现在还会产生"的状态，也不要贸然删这些判断——
    //   删了历史记录的显示和统计会变。
    Holiday     = 5,
    OnLeave     = 6,  // 请假：已通过请假审批
    Overtime    = 7,  // 加班
    NotPunched  = 8,  // 未打卡：打了上班卡但漏打下班卡（或反之）
    BusinessTrip = 9  // 出差：已通过出差审批，出差期间算全勤，不需要打卡
}
