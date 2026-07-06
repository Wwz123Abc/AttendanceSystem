using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

// 本文件放的是和审批相关的 DTO（在网页/接口和后台之间传递审批数据的简单类）。

// ── 提交 / 处理 ───────────────────────────────────────────────────────────────

/// <summary>提交审批申请时，网页传给后台的数据（补卡/请假/加班共用一个）。</summary>
public class SubmitApprovalDto
{
    public ApprovalType ApprovalType { get; set; }   // 申请类型：补卡/请假/加班

    // 补卡时填这几项
    public DateOnly?  PunchDate { get; set; }
    public PunchType? PunchType { get; set; }
    public TimeOnly?  PunchTime { get; set; }

    // 请假时填这几项
    public LeaveType? LeaveType      { get; set; }
    public DateTime?  LeaveStartTime { get; set; }
    public DateTime?  LeaveEndTime   { get; set; }

    // 加班时填这几项
    public DateTime? OvertimeStartTime { get; set; }
    public DateTime? OvertimeEndTime   { get; set; }

    // 出差时填这几项
    public DateTime? BusinessTripStartTime   { get; set; }
    public DateTime? BusinessTripEndTime     { get; set; }
    public string?   BusinessTripDestination { get; set; }

    // 通用
    public string?       Reason         { get; set; }        // 申请理由
    public List<string>  AttachmentUrls { get; set; } = [];  // 附件地址列表
}

/// <summary>审批人处理申请时传的数据。</summary>
public class HandleApprovalDto
{
    public int    ApprovalRequestId { get; set; }   // 处理哪张申请单
    public bool   IsApproved        { get; set; }   // 通过(true) 还是 驳回(false)
    public string? Comment          { get; set; }   // 审批意见（驳回时必填）
}

// ── 查询 ─────────────────────────────────────────────────────────────────────

/// <summary>审批记录查询条件（分页 + 多条件过滤）。</summary>
public class ApprovalQueryDto
{
    public int?            ApplicantUserId { get; set; }   // 按申请人
    public ApprovalType?   ApprovalType    { get; set; }   // 按类型
    public ApprovalStatus? ApprovalStatus  { get; set; }   // 按状态
    public DateTime?       StartDate       { get; set; }   // 提交时间起
    public DateTime?       EndDate         { get; set; }   // 提交时间止
    public int             PageIndex       { get; set; } = 1;    // 第几页
    public int             PageSize        { get; set; } = 20;   // 每页几条
}

// ── 展示 ─────────────────────────────────────────────────────────────────────

/// <summary>审批申请展示 DTO（用于列表/详情页显示一张申请单的全部信息）。</summary>
public class ApprovalRequestDto
{
    public int    Id                  { get; set; }
    public string RequestNo           { get; set; } = string.Empty;   // 申请单号
    public string ApplicantName       { get; set; } = string.Empty;   // 申请人姓名
    public string ApplicantEmployeeNo { get; set; } = string.Empty;   // 申请人工号
    public string? DeptName           { get; set; }                   // 申请人部门

    public ApprovalType   ApprovalType       { get; set; }
    public string         ApprovalTypeText   { get; set; } = string.Empty;   // 类型中文名
    public ApprovalStatus ApprovalStatus     { get; set; }
    public string         ApprovalStatusText { get; set; } = string.Empty;   // 状态中文名
    public string         ApprovalStatusCss  { get; set; } = string.Empty;   // 状态颜色样式

    // 补卡信息
    public DateOnly?  PunchDate { get; set; }
    public PunchType? PunchType { get; set; }
    public TimeOnly?  PunchTime { get; set; }

    // 请假信息
    public LeaveType? LeaveType          { get; set; }
    public string?    LeaveTypeText       { get; set; }   // 请假类型中文名
    public DateTime?  LeaveStartTime     { get; set; }
    public DateTime?  LeaveEndTime       { get; set; }
    public decimal?   LeaveDurationHours { get; set; }

    // 加班信息
    public DateTime? OvertimeStartTime     { get; set; }
    public DateTime? OvertimeEndTime       { get; set; }
    public decimal?  OvertimeDurationHours { get; set; }

    // 出差信息
    public DateTime? BusinessTripStartTime    { get; set; }
    public DateTime? BusinessTripEndTime      { get; set; }
    public decimal?  BusinessTripDurationDays { get; set; }
    public string?   BusinessTripDestination  { get; set; }

    // 通用
    public string?       Reason         { get; set; }
    public List<string>  AttachmentUrls { get; set; } = [];

    public DateTime SubmittedAt     { get; set; }
    public string   SubmittedAtText => SubmittedAt.ToString("yyyy-MM-dd HH:mm");   // 提交时间文字

    public List<ApprovalStepDto> Steps { get; set; } = [];   // 各级审批节点
}

/// <summary>审批节点展示 DTO（一条审批记录：谁审的、结果、意见）。</summary>
public class ApprovalStepDto
{
    public int            StepOrder    { get; set; }                  // 第几级
    public string         ApproverName { get; set; } = string.Empty;  // 审批人姓名
    public ApprovalStatus Status       { get; set; }                  // 处理结果
    public string         StatusText   { get; set; } = string.Empty;  // 结果中文名
    public string?        Comment      { get; set; }                  // 审批意见
    public DateTime?      HandledAt    { get; set; }                  // 处理时间
    public string         HandledAtText => HandledAt?.ToString("yyyy-MM-dd HH:mm") ?? "待处理";  // 处理时间文字
}
