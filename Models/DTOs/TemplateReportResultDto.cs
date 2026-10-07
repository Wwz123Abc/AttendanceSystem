using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>“模板月度汇总表”整体结果：统计周期 + 每一天的日期表头 + 每个员工一行。</summary>
public class TemplateReportResultDto
{
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate   { get; set; }

    /// <summary>周期内每一天，按顺序排列（和每行的 DailyHours 下标一一对应）</summary>
    public List<DateOnly> Dates { get; set; } = [];

    public List<TemplateReportRowDto> Rows { get; set; } = [];
}
