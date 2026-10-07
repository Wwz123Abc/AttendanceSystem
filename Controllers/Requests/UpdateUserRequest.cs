using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Controllers;

// 修改员工的请求体：装表单字段的简洁数据载体。
public record UpdateUserRequest(
    string    EmployeeNo,
    string    RealName,
    int?      DepartmentId,
    string?   Position,
    UserRole  Role,
    int?      AttendanceGroupId,
    int?      SupervisorUserId,
    string?   Phone,
    string?   Email,
    string?   IdNumber,
    DateOnly? HireDate,
    // null = 这次不改动设备分配；传了（哪怕空数组）就按传入集合全量覆盖
    List<int>? DeviceIds = null,
    int?       ScopedDepartmentId = null);
