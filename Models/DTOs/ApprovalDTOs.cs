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

    /// <summary>
    /// 员工自己选的审批人编号。如果所在考勤组配了审批人名单，这里必须选一个名单里的人；
    /// 组里没配审批人名单时可以不填，系统会自动退回直属上级/兜底管理员。
    /// </summary>
    public int? ApproverUserId { get; set; }
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
    public string?         Keyword         { get; set; }   // 关键字：申请人姓名 / 工号 / 申请单号
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

    /// <summary>申请内容的一句话描述（总审批记录列表/导出用）：补卡=补哪天几点的什么卡；请假/加班/出差=起止时间。</summary>
    public string ContentText => ApprovalType switch
    {
        ApprovalType.PunchReplenishment =>
            $"补{(PunchType == Enums.PunchType.ClockOut ? "下班" : "上班")}卡 {PunchDate:yyyy-MM-dd} {PunchTime:HH\\:mm}",
        ApprovalType.Leave =>
            $"{LeaveTypeText}：{LeaveStartTime:yyyy-MM-dd HH:mm} ~ {LeaveEndTime:yyyy-MM-dd HH:mm}",
        ApprovalType.Overtime =>
            $"{OvertimeStartTime:yyyy-MM-dd HH:mm} ~ {OvertimeEndTime:yyyy-MM-dd HH:mm}",
        ApprovalType.BusinessTrip =>
            $"{BusinessTripStartTime:yyyy-MM-dd HH:mm} ~ {BusinessTripEndTime:yyyy-MM-dd HH:mm}"
            + (string.IsNullOrWhiteSpace(BusinessTripDestination) ? "" : $"，目的地：{BusinessTripDestination}"),
        _ => ""
    };

    /// <summary>时长文字：请假/加班是小时，出差是天，补卡没有时长。</summary>
    public string DurationText => ApprovalType switch
    {
        ApprovalType.Leave        => LeaveDurationHours.HasValue ? $"{LeaveDurationHours:0.##} 小时" : "",
        ApprovalType.Overtime     => OvertimeDurationHours.HasValue ? $"{OvertimeDurationHours:0.##} 小时" : "",
        ApprovalType.BusinessTrip => BusinessTripDurationDays.HasValue ? $"{BusinessTripDurationDays:0.#} 天" : "",
        _                         => ""
    };

    /// <summary>各级审批的一段文字（导出用，每级一行）：几级 审批人：结果（处理时间）[意见]。</summary>
    public string StepsText => string.Join("\n", Steps.Select(s =>
        $"第{s.StepOrder}级 {s.ApproverName}：{s.StatusText}"
        + (s.HandledAt.HasValue ? $"（{s.HandledAt:yyyy-MM-dd HH:mm}）" : "")
        + (string.IsNullOrWhiteSpace(s.Comment) ? "" : $"「{s.Comment}」")));
}

/// <summary>可选审批人 DTO（员工提交申请时，下拉框里的一个候选人）。</summary>
public class ApproverOptionDto
{
    public int     UserId   { get; set; }                   // 这个候选审批人的用户编号
    public string  RealName { get; set; } = string.Empty;    // 姓名
    public string? Position { get; set; }                   // 岗位（辅助员工分辨选谁）
}

/// <summary>审批节点展示 DTO（一条审批记录：谁审的、结果、意见）。</summary>
public class ApprovalStepDto
{
    public int            StepOrder      { get; set; }                  // 第几级
    public int            ApproverUserId { get; set; }                  // 审批人编号（用来判断"这一级是不是我"）
    public string         ApproverName   { get; set; } = string.Empty;  // 审批人姓名
    public ApprovalStatus Status       { get; set; }                  // 处理结果
    public string         StatusText   { get; set; } = string.Empty;  // 结果中文名
    public string?        Comment      { get; set; }                  // 审批意见
    public DateTime?      HandledAt    { get; set; }                  // 处理时间
    public string         HandledAtText => HandledAt?.ToString("yyyy-MM-dd HH:mm") ?? "待处理";  // 处理时间文字
}
