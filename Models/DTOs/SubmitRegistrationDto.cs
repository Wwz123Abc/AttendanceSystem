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
    public int?    DepartmentId          { get; set; }                  // 意向部门（从二维码链接的 deptId 参数带过来）
}
