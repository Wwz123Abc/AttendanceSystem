namespace AttendanceSystem.Models.Enums;

/// <summary>请假类型：请的是哪种假。</summary>
public enum LeaveType
{
    PersonalLeave     = 1,  // 事假
    SickLeave         = 2,  // 病假
    AnnualLeave       = 3,  // 年假
    MarriageLeave     = 4,  // 婚假
    MaternityLeave    = 5,  // 产假
    BereavementLeave  = 6,  // 丧假
    CompensatoryLeave = 7   // 调休
}
