using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Controllers;

// 修改班次的请求体：原因同 CreateShiftRequest（不直接绑 ShiftSchedule 实体）。
public record UpdateShiftRequest(
    string    ShiftName,
    ShiftType ShiftType,
    TimeOnly  WorkStartTime,
    TimeOnly  WorkEndTime,
    int       LateToleranceMinutes,
    int       EarlyLeaveToleranceMinutes,
    int       OvertimeThresholdMinutes,
    bool      IsCrossDay,
    decimal   StandardWorkHours,
    string    Color,
    // 新增（可空：null = 本次不修改该字段；传值 = 覆盖）
    int?      EarliestClockInMinutes = null,   // 最多提前打卡分钟数
    string?   RestDaysOfWeek = null,           // 每周休息日，如 "0,6"（0=周日…6=周六）
    string?   MidCheckWindows = null);         // 午间必打卡窗口（格式同 Create 接口；""=清空）
