using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>审批记录查询条件（分页 + 多条件过滤）。</summary>
public class ApprovalQueryDto
{
    public int?            ApplicantUserId { get; set; }   // 按申请人
    public ApprovalType?   ApprovalType    { get; set; }   // 按类型
    public ApprovalStatus? ApprovalStatus  { get; set; }   // 按状态
    public string?         Keyword         { get; set; }   // 关键字：申请人姓名 / 工号 / 申请单号
    public DateTime?       StartDate       { get; set; }   // 提交时间起
    public DateTime?       EndDate         { get; set; }   // 提交时间止
    public int             PageIndex       { get; set; } = 1;    // 第几页
    public int             PageSize        { get; set; } = 20;   // 每页几条
}
