namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 每晚自动核对的结果留档（2026-11-03 用户要求）：每晚核对跑完后把疑点写进来，数据核对页按"当前周"展示；
/// 每周日夜里清掉上周的，只保留当前周（周一~周日）的数据。
/// 纯留档，不参与任何考勤/工资计算；核对逻辑本身仍是 <c>AttendanceService.AuditAsync</c>（只读）。
/// </summary>
public class AuditIssueLog
{
    public int      Id         { get; set; }

    /// <summary>这次核对是哪一晚跑的（运行日）。</summary>
    public DateOnly RunDate    { get; set; }

    /// <summary>疑点所在的考勤日。</summary>
    public DateOnly WorkDate   { get; set; }

    public int      UserId     { get; set; }
    public string   EmployeeNo { get; set; } = "";
    public string   RealName   { get; set; } = "";
    public string?  GroupName  { get; set; }

    /// <summary>疑点类型，取值见 <c>AttendanceService.AuditHours</c> 等常量。</summary>
    public string   IssueType  { get; set; } = "";

    /// <summary>danger = 工资可能算错；warning = 缺前提、需人工确认。</summary>
    public string   Severity   { get; set; } = "";

    public string   Detail     { get; set; } = "";

    /// <summary>系统里存的值（工时/加班小时），没有就为空。</summary>
    public decimal? Stored     { get; set; }

    /// <summary>按现行规则算出来应该是多少，没有就为空。</summary>
    public decimal? Expected   { get; set; }

    public DateTime CreatedAt  { get; set; } = DateTime.Now;
}
