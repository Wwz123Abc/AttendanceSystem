using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

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
