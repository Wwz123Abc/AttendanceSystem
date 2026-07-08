using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

// 本文件放的是"员工扫码自助登记"相关的 DTO。

/// <summary>员工扫码提交登记时，网页传给后台的数据。</summary>
public class SubmitRegistrationDto
{
    public string RealName { get; set; } = string.Empty;   // 姓名
    public string Phone    { get; set; } = string.Empty;   // 手机号
    public string IdNumber { get; set; } = string.Empty;    // 身份证号
}

/// <summary>员工登记展示 DTO（管理员"待确认"列表里的一行）。</summary>
public class EmployeeRegistrationDto
{
    public int    Id       { get; set; }
    public string RealName { get; set; } = string.Empty;
    public string Phone    { get; set; } = string.Empty;
    public string IdNumber { get; set; } = string.Empty;

    public RegistrationStatus Status     { get; set; }
    public string             StatusText { get; set; } = string.Empty;

    public DateTime SubmittedAt { get; set; }
    public string   SubmittedAtText => SubmittedAt.ToString("yyyy-MM-dd HH:mm");
}
