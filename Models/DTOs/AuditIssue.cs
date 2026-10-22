namespace AttendanceSystem.Models.DTOs;

/// <summary>
/// "数据核对"发现的一条疑点：某个人某一天，系统里存的数和按当前规则从原始打卡/班次/加班单重新算出来的数对不上，
/// 或者缺少算对所需要的前提（比如有打卡但没排班）。只读检查，不改任何数据。
/// </summary>
/// <param name="Severity">danger = 工资可能算错；warning = 前提缺失或口径风险，需要人工确认。</param>
/// <param name="Type">疑点类型，见 <c>AttendanceService.Audit</c>。</param>
public record AuditIssue(
    int UserId, string EmployeeNo, string RealName, string? GroupName, DateOnly WorkDate,
    string Type, string Severity, string Detail, decimal? Stored, decimal? Expected);
