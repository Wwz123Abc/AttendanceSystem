using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>员工"考勤核对"页的一行：某一天的考勤记录 + 那天的异议状态。</summary>
public class AttendanceVerifyRowDto
{
    public DateOnly WorkDate { get; set; }
    public string   DayOfWeekText { get; set; } = "";   // 周一/周二…
    public string?  ShiftName     { get; set; }
    public string?  ShiftTimeText { get; set; }         // 如 08:30–17:30、20:00–次日 05:30

    /// <summary>这天有没有考勤记录（没有记录也能提"我上班了但没记录"）。</summary>
    public bool     HasRecord     { get; set; }
    /// <summary>这天有没有排班（没排班的人也能核对，但会提示）。</summary>
    public bool     IsScheduled   { get; set; }

    public string   ClockInText  { get; set; } = "--";  // 上班卡（跨天的会带日期，如 11-04 08:04）
    public string   ClockOutText { get; set; } = "--";  // 下班卡

    public decimal  WorkHours            { get; set; }   // 正班工时
    public decimal  OvertimeHours        { get; set; }   // 已计入的加班工时
    public decimal  PendingOvertimeHours { get; set; }   // 那天有、但还没审完的加班小时
    /// <summary>那天没审完的加班单说明，如 "2 小时（加班单待审批）"；没有则为空。</summary>
    public List<string> PendingOvertimeTexts { get; set; } = [];

    public string   StatusText     { get; set; } = "";
    public string   StatusCssClass { get; set; } = "";
    public int      LateMinutes       { get; set; }
    public int      EarlyLeaveMinutes { get; set; }

    // 那天已有的异议（一天多张时展示最近一张）
    public int?     DisputeId         { get; set; }
    public AttendanceDisputeStatus? DisputeStatus { get; set; }
    public string?  DisputeTypeText   { get; set; }
    public string?  DisputeReply      { get; set; }
    public string?  DisputeStatusText => DisputeStatus?.Text();
    public string?  DisputeStatusCssClass => DisputeStatus?.CssClass();
}

/// <summary>管理端"考勤异议"列表的一行。</summary>
public class AttendanceDisputeListItemDto
{
    public int      Id         { get; set; }
    public int      UserId     { get; set; }
    public string   EmployeeNo { get; set; } = "";
    public string   RealName   { get; set; } = "";
    public string?  DeptName   { get; set; }
    public DateOnly WorkDate   { get; set; }
    public string   DayOfWeekText { get; set; } = "";
    public AttendanceDisputeType Type { get; set; }
    public string   TypeText   { get; set; } = "";
    public string   Reason     { get; set; } = "";
    public string?  ExpectedText { get; set; }
    public string?  Snapshot   { get; set; }
    public AttendanceDisputeStatus Status { get; set; }
    public string   StatusText     => Status.Text();
    public string   StatusCssClass => Status.CssClass();
    public DateTime CreatedAt  { get; set; }
    public string?  HandlerName { get; set; }
    public DateTime? HandledAt  { get; set; }
    public string?  Reply       { get; set; }
}

/// <summary>管理端列表的筛选条件。</summary>
public class AttendanceDisputeQueryDto
{
    /// <summary>不传 = 全部状态。</summary>
    public AttendanceDisputeStatus? Status { get; set; }
    public int Page     { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class AttendanceDisputeListResultDto
{
    public List<AttendanceDisputeListItemDto> Items { get; set; } = [];
    public int Total { get; set; }
    /// <summary>待处理（含处理中）总数，给页面顶部提示用。</summary>
    public int PendingTotal { get; set; }
}
