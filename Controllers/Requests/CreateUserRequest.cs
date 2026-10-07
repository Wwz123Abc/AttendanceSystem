using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Controllers;

// 新增员工的请求体：装表单字段的简洁数据载体。
public record CreateUserRequest(
    string    EmployeeNo,
    string    RealName,
    int?      DepartmentId,
    string?   Position,
    UserRole  Role,
    int?      AttendanceGroupId,
    int?      SupervisorUserId,
    string?   Phone,
    string?   Email,
    // 身份证号：必须能通过这个接口存上，否则黑名单身份证查重（CreateUserAsync 里按
    // IdNumber 精确匹配）对走 API 建档的员工完全不生效，等于形同虚设
    string?   IdNumber,
    DateOnly? HireDate,
    // 这个人要推送到哪几台考勤机（不传/传空 = 不指定任何设备）；初始密码不再由调用方指定，
    // 统一固定为 123456，见 CreateUser 方法内部
    List<int>? DeviceIds = null,
    // 管理范围：只有总部超级管理员的这个值会被采纳（null=不受限）；分公司管理员/文员调这个接口时
    // 传什么都会被忽略，新账号强制钳到操作者自己的范围，见 CreateUser 方法内部
    int?       ScopedDepartmentId = null);
