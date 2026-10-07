using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Controllers;

// 新增班次的请求体：装表单字段的简洁数据载体。
// 不直接绑 ShiftSchedule 实体：它的 AttendanceGroup 导航属性是非空引用类型，
// [ApiController] 会把没传这个字段的请求当成校验失败直接 400，调用方只该传 AttendanceGroupId。
public record CreateShiftRequest(
    int       AttendanceGroupId,
    string    ShiftName,
    TimeOnly  WorkStartTime,
    TimeOnly  WorkEndTime,
    ShiftType ShiftType = ShiftType.Fixed,
    int       LateToleranceMinutes = 5,
    int       EarlyLeaveToleranceMinutes = 5,
    int       EarliestClockInMinutes = 60,
    int       OvertimeThresholdMinutes = 30,
    bool      IsCrossDay = false,
    decimal   StandardWorkHours = 8,
    string    Color = "#1890ff",
    string    RestDaysOfWeek = "0,6",
    string?   MidCheckWindows = null);
