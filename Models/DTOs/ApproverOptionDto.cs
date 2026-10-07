namespace AttendanceSystem.Models.DTOs;

/// <summary>可选审批人 DTO（员工提交申请时，下拉框里的一个候选人）。</summary>
public class ApproverOptionDto
{
    public int     UserId   { get; set; }                   // 这个候选审批人的用户编号
    public string  RealName { get; set; } = string.Empty;    // 姓名
    public string? Position { get; set; }                   // 岗位（辅助员工分辨选谁）
}
