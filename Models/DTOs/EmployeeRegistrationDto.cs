using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>员工登记展示 DTO（管理员"待确认"列表里的一行）。</summary>
public class EmployeeRegistrationDto
{
    public int    Id       { get; set; }
    public string RealName { get; set; } = string.Empty;
    public string Phone    { get; set; } = string.Empty;
    public string IdNumber { get; set; } = string.Empty;

    public string? Position              { get; set; }
    public string? ContractCompany       { get; set; }
    public string? HomeAddress           { get; set; }
    public string? EmergencyContactName  { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? IdCardPhotoUrl        { get; set; }

    public int?    DepartmentId { get; set; }
    public string? DeptName     { get; set; }   // 意向部门名（没有则说明是旧版通用链接提交的，显示"未指定"）

    public RegistrationStatus Status     { get; set; }
    public string             StatusText { get; set; } = string.Empty;

    public DateTime SubmittedAt { get; set; }
    public string   SubmittedAtText => SubmittedAt.ToString("yyyy-MM-dd HH:mm");
}
