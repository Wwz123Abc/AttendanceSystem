using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

// 本文件放的是"员工扫码自助登记"相关的 DTO。

/// <summary>员工扫码提交登记时，网页传给后台的数据。</summary>
public class SubmitRegistrationDto
{
    public string  RealName              { get; set; } = string.Empty;   // 姓名
    public string  Phone                 { get; set; } = string.Empty;   // 手机号
    public string  IdNumber              { get; set; } = string.Empty;   // 身份证号
    public string? Position              { get; set; }                  // 岗位（固定选项之一）
    public string? ContractCompany       { get; set; }                  // 劳务公司
    public string? HomeAddress           { get; set; }                  // 家庭住址
    public string? EmergencyContactName  { get; set; }                  // 紧急联系人姓名
    public string? EmergencyContactPhone { get; set; }                  // 紧急联系人电话
    public string? IdCardPhotoUrl        { get; set; }                  // 身份证照片地址（页面已经存好文件，这里只传地址）
}

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

    public RegistrationStatus Status     { get; set; }
    public string             StatusText { get; set; } = string.Empty;

    public DateTime SubmittedAt { get; set; }
    public string   SubmittedAtText => SubmittedAt.ToString("yyyy-MM-dd HH:mm");
}
