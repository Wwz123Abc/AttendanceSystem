using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Report;

/// <summary>月度报表页：按月看部门考勤汇总，并导出汇总表/个人明细 Excel。需管理员/文员。</summary>
[Authorize(Policy = "ManagePolicy")]
public class MonthlyReportModel(IAttendanceService attendanceService) : PageModel
{
    // Excel 文件的类型标识
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public List<MonthlySummaryDto> Summaries { get; set; } = [];
    public int Year  { get; set; }
    public int Month { get; set; }

    /// <summary>打开页面：先按月生成最新汇总，再读出来显示。</summary>
    public async Task OnGetAsync(int? year, int? month)
    {
        Year  = year  ?? DateTime.Today.Year;
        Month = month ?? DateTime.Today.Month;

        await attendanceService.GenerateMonthlySummaryAsync(Year, Month);                    // 先算
        Summaries = await attendanceService.GetDeptMonthlySummariesAsync(null, null, Year, Month);  // 再取
    }

    /// <summary>点“导出汇总表”时执行。</summary>
    public async Task<IActionResult> OnGetExportSummaryAsync(int year, int month)
    {
        await attendanceService.GenerateMonthlySummaryAsync(year, month);
        var data  = await attendanceService.GetDeptMonthlySummariesAsync(null, null, year, month);
        var bytes = ExcelExportHelper.ExportMonthlySummary(data, year, month);
        return File(bytes, XlsxContentType, $"考勤汇总_{year}年{month:D2}月.xlsx");
    }

    /// <summary>点某人“导出明细”时执行。</summary>
    public async Task<IActionResult> OnGetExportDetailAsync(int userId, int year, int month)
    {
        await attendanceService.GenerateMonthlySummaryAsync(year, month);
        var all    = await attendanceService.GetDeptMonthlySummariesAsync(null, null, year, month);
        var target = all.FirstOrDefault(s => s.UserId == userId);   // 找这个人的汇总
        if (target is null) return NotFound();

        // 补上这个人的每日明细（汇总列表里默认不带明细）
        var records = await attendanceService.GetPersonalAttendanceAsync(new PersonalAttendanceQueryDto
        {
            UserId = userId, Year = year, Month = month
        });
        target.DailyRecords = records;

        var bytes = ExcelExportHelper.ExportDailyStatusReport(target);
        return File(bytes, XlsxContentType, $"{target.RealName}_{year}年{month:D2}月考勤明细.xlsx");
    }
}
