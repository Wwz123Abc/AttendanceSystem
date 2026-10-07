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
