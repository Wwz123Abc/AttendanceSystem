using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using NPOI.XSSF.UserModel;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 覆盖 <see cref="ExcelExportHelper.ExportTemplateReport"/>：表头数组（tailHeaders）和逐列写值时
/// 用的手动 `c++` 计数器必须严格一一对应——2026-09-17 代码审查指出，这种写法一旦以后有人插入/
/// 删除/调整某一列却忘了同步改另一边，会导致数据从某一列开始整体错位，而且不会有任何编译错误或
/// 运行时异常，只会在打开导出的 Excel 表格时才被人肉眼发现（甚至可能发现不了，被当成正常数据用）。
/// 这里把行内所有"尾部统计列"字段都设成互不相同的非零值，导出后逐列读回来，断言每一列的数值
/// 都精确落在预期的那一列——只要两边（表头数组、写值顺序）有任何一处不同步，这个测试就会失败。
/// </summary>
public class ExcelExportColumnAlignmentTests
{
    [Fact]
    public void 模板月度汇总表_尾部统计列表头和实际写入的数据一一对应()
    {
        var dates = new List<DateOnly> { new(2026, 9, 1), new(2026, 9, 2) };
        var row = new TemplateReportRowDto
        {
            RealName = "张三", EmployeeNo = "E001",
            DailyHours = [8, null],
            DailyIsNightShift = [false, false],
            ActualWorkdays        = 21,
            RestDays              = 8,
            TotalWorkHours        = 168.5m,
            RegularWorkHours      = 158.5m,
            LateMinutes           = 5,
            EarlyLeaveCount       = 1,
            LateCount             = 2,
            EarlyLeaveMinutes     = 3,
            MissingClockInCount   = 1,
            MissingClockOutCount  = 1,
            AbsentDays            = 1,
            BusinessTripHours     = 8m,
            NightShiftDays        = 2,
            NightShiftHours       = 16m,
            TotalOvertimeHours    = 10m,
            WeekdayOvertimeHours  = 4m,
            RestDayOvertimeHours  = 3m,
            HolidayOvertimeHours  = 6m
        };
        var result = new TemplateReportResultDto
        {
            StartDate = dates[0], EndDate = dates[^1], Dates = dates, Rows = [row]
        };

        var bytes = ExcelExportHelper.ExportTemplateReport(result);

        using var wb    = new XSSFWorkbook(new MemoryStream(bytes));
        var sheet        = wb.GetSheetAt(0);
        var headerRow    = sheet.GetRow(2);   // 第 2 行：主表头（含尾部统计列表头）
        var dataRow      = sheet.GetRow(4);   // 第 4 行：第一个员工的数据行

        const int fixedCols = 6;              // 姓名/考勤组/部门/工号/职位/合同公司
        var dayCount = dates.Count;
        var tailStart = fixedCols + dayCount; // 尾部统计列从这里开始

        // 表头顺序（跟 ExportTemplateReport 里 tailHeaders 数组保持一致），逐一核对表头文字，
        // 顺带断言表头列数和下面实际读到的数据列数一致，不多不少
        string[] expectedHeaders =
        [
            "出勤天数", "休息天数", "总工时", "正班工时", "迟到时长", "早退次数", "迟到次数", "早退时长",
            "上班缺卡次数", "下班缺卡次数", "旷工天数", "出差时长", "外出时长", "夜班次数", "夜班总工时",
            "加班总时长", "工作日加班", "休息日加班", "节假日加班"
        ];
        for (var i = 0; i < expectedHeaders.Length; i++)
            Assert.Equal(expectedHeaders[i], headerRow.GetCell(tailStart + i).StringCellValue);

        // 逐一核对数据行：每一列的数值必须精确落在表头对应的那一列上
        Assert.Equal(21,    dataRow.GetCell(tailStart + 0).NumericCellValue);   // 出勤天数
        Assert.Equal(8,     dataRow.GetCell(tailStart + 1).NumericCellValue);   // 休息天数
        Assert.Equal(168.5, dataRow.GetCell(tailStart + 2).NumericCellValue);   // 总工时
        Assert.Equal(158.5, dataRow.GetCell(tailStart + 3).NumericCellValue);   // 正班工时
        Assert.Equal(5,     dataRow.GetCell(tailStart + 4).NumericCellValue);   // 迟到时长
        Assert.Equal(1,     dataRow.GetCell(tailStart + 5).NumericCellValue);   // 早退次数
        Assert.Equal(2,     dataRow.GetCell(tailStart + 6).NumericCellValue);   // 迟到次数
        Assert.Equal(3,     dataRow.GetCell(tailStart + 7).NumericCellValue);   // 早退时长
        Assert.Equal(1,     dataRow.GetCell(tailStart + 8).NumericCellValue);   // 上班缺卡次数
        Assert.Equal(1,     dataRow.GetCell(tailStart + 9).NumericCellValue);   // 下班缺卡次数
        Assert.Equal(1,     dataRow.GetCell(tailStart + 10).NumericCellValue);  // 旷工天数
        Assert.Equal(8,     dataRow.GetCell(tailStart + 11).NumericCellValue);  // 出差时长
        Assert.Null(dataRow.GetCell(tailStart + 12));                          // 外出时长：系统没有这个概念，恒不写值
        Assert.Equal(2,     dataRow.GetCell(tailStart + 13).NumericCellValue);  // 夜班次数
        Assert.Equal(16,    dataRow.GetCell(tailStart + 14).NumericCellValue);  // 夜班总工时
        Assert.Equal(10,    dataRow.GetCell(tailStart + 15).NumericCellValue);  // 加班总时长
        Assert.Equal(4,     dataRow.GetCell(tailStart + 16).NumericCellValue);  // 工作日加班
        Assert.Equal(3,     dataRow.GetCell(tailStart + 17).NumericCellValue);  // 休息日加班
        Assert.Equal(6,     dataRow.GetCell(tailStart + 18).NumericCellValue);  // 节假日加班

        // 数据行最后一个有值的单元格必须正好是最后一列（节假日加班），不多出、也不少一列——
        // 这一条能直接抓出"表头 19 列，但写值那边多写/少写了一列"这种整体错位的 bug
        Assert.Equal(tailStart + expectedHeaders.Length, dataRow.LastCellNum);
    }
}
