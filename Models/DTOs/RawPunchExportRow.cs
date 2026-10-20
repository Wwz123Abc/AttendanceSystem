namespace AttendanceSystem.Models.DTOs;

/// <summary>"导出员工原始打卡记录"里的一行：一次打卡一行，不做任何合并和取舍。</summary>
public record RawPunchExportRow(
    string EmployeeNo, string RealName, string? DeptName, string? GroupName,
    DateTime PunchTime, int PunchType, string? DeviceInfo, string? Address,
    double? Latitude, double? Longitude, bool? LocationValid, bool IsValid, DateTime CreatedAt);
