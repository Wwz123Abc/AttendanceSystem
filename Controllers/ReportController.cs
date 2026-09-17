using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Controllers;

/// <summary>报表接口：导出 Excel、手动生成月度汇总。仅管理员/文员。分公司管理员只能导出/生成
/// 自己范围内的数据。</summary>
[Authorize(Policy = "ManagePolicy")]
[Route("api/[controller]")]
[ApiController]
public class ReportController(IAttendanceService attendanceService, IDeptScopeService deptScopeService, AttendanceDbContext db) : ControllerBase
{
    // Excel 文件的标准类型标识（告诉浏览器这是个 .xlsx 文件）
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>导出月度考勤汇总表（报表1）。</summary>
    [HttpGet("export/monthly-summary")]
    public async Task<IActionResult> ExportMonthlySummary(
        [FromQuery] int? deptId, [FromQuery] int? groupId,
        [FromQuery] int year, [FromQuery] int month)
    {
        var cu = HttpContext.GetCurrentUser()!;
        deptId = await deptScopeService.ResolveEffectiveDeptIdAsync(cu, deptId);
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
        var summaries = await attendanceService.GetDeptMonthlySummariesAsync(deptId, groupId, year, month, visibleIds);  // 取数据
        var bytes     = ExcelExportHelper.ExportMonthlySummary(summaries, year, month);                      // 生成 Excel
        var fileName  = $"月度考勤汇总_{year}年{month:D2}月.xlsx";
        // 文件名含中文，要 UrlEncode 编码，避免浏览器下载时乱码
        return File(bytes, XlsxContentType, System.Web.HttpUtility.UrlEncode(fileName));
    }

    /// <summary>导出某员工的每日考勤明细（报表2）。</summary>
    [HttpGet("export/daily-status/{userId:int}")]
    public async Task<IActionResult> ExportDailyStatus(
        int userId, [FromQuery] int year, [FromQuery] int month)
    {
        // userId 是路由参数，受限管理员完全可以直接改 URL 里的 id 导出别的分公司员工的考勤明细，
        // 这里必须先校验目标员工在不在自己范围内
        var targetDeptId = await db.Users.Where(u => u.Id == userId).Select(u => (int?)u.DepartmentId).FirstOrDefaultAsync();
        if (!await deptScopeService.CanAccessDeptAsync(HttpContext.GetCurrentUser()!, targetDeptId))
            return Forbid();

        var summary = await attendanceService.GetMonthlySummaryAsync(userId, year, month);
        if (summary is null)   // 还没生成汇总，导不出
            return NotFound(new { Success = false, Message = "未找到对应汇总数据，请先生成月度汇总" });

        var bytes    = ExcelExportHelper.ExportDailyStatusReport(summary);
        var fileName = $"{summary.RealName}_每日考勤_{year}年{month:D2}月.xlsx";
        return File(bytes, XlsxContentType, System.Web.HttpUtility.UrlEncode(fileName));
    }

    /// <summary>手动生成某月的考勤汇总（不想等月初自动生成时用）。受限管理员只重算自己范围内的人，
    /// 不会影响其他分公司的汇总数据。</summary>
    [HttpPost("generate-monthly-summary")]
    public async Task<IActionResult> GenerateMonthlySummary(
        [FromQuery] int year, [FromQuery] int month)
    {
        var cu = HttpContext.GetCurrentUser()!;
        if (!cu.IsScoped)
        {
            await attendanceService.GenerateMonthlySummaryAsync(year, month);
        }
        else
        {
            var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(cu);
            var userIds = await db.Users.Where(u => u.DepartmentId != null && visibleIds!.Contains(u.DepartmentId.Value))
                .Select(u => u.Id).ToListAsync();
            // 一次调用把这批人整批传进去（原来是逐人循环调用，等于一遍遍重复批量查同一个月的
            // 考勤记录/排班/假期表，人数一多这个接口会明显变慢）
            await attendanceService.GenerateMonthlySummaryAsync(year, month, userIds);
        }
        return Ok(new { Success = true, Message = $"{year}年{month}月考勤汇总已生成" });
    }
}
