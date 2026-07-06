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
    Holiday     = 5,  // 休假：节假日 / 休息日，本就不用上班
    OnLeave     = 6,  // 请假：已通过请假审批
    Overtime    = 7,  // 加班
    NotPunched  = 8,  // 未打卡：打了上班卡但漏打下班卡（或反之）
    BusinessTrip = 9  // 出差：已通过出差审批，出差期间算全勤，不需要打卡
}

/// <summary>打卡类型：这次打卡是上班还是下班。</summary>
public enum PunchType
{
    ClockIn  = 1,  // 上班打卡
    ClockOut = 2   // 下班打卡
}

/// <summary>班次类型：这个班次的上下班时间规则是哪一种。</summary>
public enum ShiftType
{
    Fixed    = 1,  // 固定班：上下班时间固定（如 9:00–18:00）
    Flexible = 2,  // 弹性班：上班时间可在一定范围内浮动
    Free     = 3   // 自由班：不限定具体时间
}

/// <summary>假期类型：这一天属于哪种特殊日子。</summary>
public enum HolidayType
{
    LegalHoliday        = 1,  // 法定节假日（如国庆），不用上班
    CompanyRestDay      = 2,  // 公司自定的休息日，不用上班
    CompensatoryWorkDay = 3   // 调班补班日：原本是周末，但要上班
}
