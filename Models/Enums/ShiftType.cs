namespace AttendanceSystem.Models.Enums;

/// <summary>班次类型：这个班次的上下班时间规则是哪一种。</summary>
public enum ShiftType
{
    Fixed    = 1,  // 固定班：上下班时间固定（如 9:00–18:00）
    Flexible = 2,  // 弹性班：上班时间可在一定范围内浮动
    Free     = 3   // 自由班：不限定具体时间
}
