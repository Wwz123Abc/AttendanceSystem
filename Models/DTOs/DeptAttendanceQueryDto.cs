using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>部门/考勤组考勤查询条件。</summary>
public class DeptAttendanceQueryDto
{
    public int?      DepartmentId      { get; set; }   // 按部门
    public int?      AttendanceGroupId { get; set; }   // 或按考勤组
    public DateOnly  StartDate         { get; set; }   // 起始日期
    public DateOnly  EndDate           { get; set; }   // 结束日期
}
