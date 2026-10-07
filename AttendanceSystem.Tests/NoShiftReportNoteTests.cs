using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 没排班员工在导出表里的"排班说明"标注。
/// </summary>
public class NoShiftReportNoteTests : SqliteTestBase
{
    // ── 没排班的人在导出表里加"排班说明"标注（正班工时没封顶、已含工作日加班）─────────────────

    [Fact]
    public void 月度汇总导出_没排班的人最后一列有标注_有排班的人是空()
    {
        var list = new List<AttendanceSystem.Models.DTOs.MonthlySummaryDto>
        {
            new() { EmployeeNo = "002582", RealName = "没排班的人", Year = 2026, Month = 9, NoShiftDays = 22, TotalWorkHours = 245.5m, TotalOvertimeHours = 170.5m },
            new() { EmployeeNo = "002874", RealName = "有排班的人", Year = 2026, Month = 9, NoShiftDays = 0,  TotalWorkHours = 160m,   TotalOvertimeHours = 177m },
        };
        var bytes = AttendanceSystem.Helpers.ExcelExportHelper.ExportMonthlySummary(list, 2026, 9);
        using var wb = new NPOI.XSSF.UserModel.XSSFWorkbook(new MemoryStream(bytes));
        var sheet = wb.GetSheetAt(0);
        Assert.Equal("排班说明", sheet.GetRow(1).GetCell(15).StringCellValue);
        Assert.Contains("没排班22天", sheet.GetRow(2).GetCell(15).StringCellValue);
        Assert.Contains("勿与加班时长直接相加", sheet.GetRow(2).GetCell(15).StringCellValue);
        Assert.Equal("", sheet.GetRow(3).GetCell(15).StringCellValue);
        Assert.Equal("实际工时(h)", sheet.GetRow(1).GetCell(13).StringCellValue);   // 原有列的位置没变
    }

    [Fact]
    public void 模板汇总导出_没排班的人最后一列有标注_原有列位置不变()
    {
        var day = new DateOnly(2026, 9, 28);
        TemplateReportRowDtoBuilder Row(string name, int noShift) => new(name, noShift);
        var rows = new List<AttendanceSystem.Models.DTOs.TemplateReportRowDto>
        {
            Row("没排班的人", 5).Build(day), Row("有排班的人", 0).Build(day),
        };
        var result = new AttendanceSystem.Models.DTOs.TemplateReportResultDto { StartDate = day, EndDate = day, Dates = [day], Rows = rows };
        var bytes = AttendanceSystem.Helpers.ExcelExportHelper.ExportTemplateReport(result);
        using var wb = new NPOI.XSSF.UserModel.XSSFWorkbook(new MemoryStream(bytes));
        var sheet = wb.GetSheetAt(0);
        var nameRow = sheet.GetRow(3);
        var last = nameRow.LastCellNum - 1;
        Assert.Equal("排班说明", nameRow.GetCell(last).StringCellValue);
        Assert.Equal("应出勤天数", nameRow.GetCell(last - 1).StringCellValue.Replace(Environment.NewLine, "").Replace("\n", ""));   // 原来的最后一列"应出勤天数"位置不变
        Assert.Contains("没排班5天", sheet.GetRow(4).GetCell(last).StringCellValue);
        Assert.Equal("", sheet.GetRow(5).GetCell(last).StringCellValue);
    }

    private sealed record TemplateReportRowDtoBuilder(string Name, int NoShift)
    {
        public AttendanceSystem.Models.DTOs.TemplateReportRowDto Build(DateOnly day) => new()
        {
            RealName = Name, NoShiftDays = NoShift,
            DailyHours = [8m], DailyIsNightShift = [false], DailyIsRest = [false],
        };
    }
}
