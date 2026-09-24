using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using NPOI.SS.UserModel;
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
///
/// 2026-09-24 导出格式调整（周末列头保留日号、筛选箭头、合计行、表头写单位、图例、休息格灰底、应出勤天数列）
/// 的回归测试也在这里。约束：新列只加在最后、不额外插入新行——发工资那边如果有程序按位置读这份表，不能错位。
/// </summary>
public class ExcelExportColumnAlignmentTests
{
    private static readonly string[] ExpectedTailHeaders =
    [
        "出勤天数", "请假天数", "休息天数", "正班工时(h)", "迟到时长(分)", "早退次数", "迟到次数", "早退时长(分)",
        "上班缺卡次数", "下班缺卡次数", "旷工天数", "出差时长(h)", "夜班次数", "夜班总工时(h)",
        "加班总时长(h)", "工作日加班(h)", "休息日加班(h)", "节假日加班(h)",
        "应出勤天数"
    ];

    private static TemplateReportRowDto FullRow(string name, string no, decimal dayHours = 8) => new()
    {
        RealName = name, EmployeeNo = no,
        DailyHours = [dayHours, null],
        DailyIsNightShift = [false, false],
        ActualWorkdays        = 21,
        LeaveDays             = 1.5m,
        RestDays              = 8,
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
        HolidayOvertimeHours  = 6m,
        ExpectedWorkdays      = 22
    };

    private static ISheet Export(List<DateOnly> dates, params TemplateReportRowDto[] rows)
    {
        var result = new TemplateReportResultDto { StartDate = dates[0], EndDate = dates[^1], Dates = dates, Rows = [.. rows] };
        var bytes = ExcelExportHelper.ExportTemplateReport(result);
        return new XSSFWorkbook(new MemoryStream(bytes)).GetSheetAt(0);
    }

    [Fact]
    public void 模板月度汇总表_尾部统计列表头和实际写入的数据一一对应()
    {
        var dates = new List<DateOnly> { new(2026, 9, 1), new(2026, 9, 2) };
        var sheet = Export(dates, FullRow("张三", "E001"));
        var headerRow = sheet.GetRow(2);   // 第 2 行：主表头（含尾部统计列表头）
        var dataRow   = sheet.GetRow(4);   // 第 4 行：第一个员工的数据行

        const int fixedCols = 6;
        var tailStart = fixedCols + dates.Count;

        for (var i = 0; i < ExpectedTailHeaders.Length; i++)
            Assert.Equal(ExpectedTailHeaders[i], headerRow.GetCell(tailStart + i).StringCellValue);

        // 逐一核对数据行：每一列的数值必须精确落在表头对应的那一列上
        Assert.Equal(21,    dataRow.GetCell(tailStart + 0).NumericCellValue);   // 出勤天数
        Assert.Equal(1.5,   dataRow.GetCell(tailStart + 1).NumericCellValue);   // 请假天数
        Assert.Equal(8,     dataRow.GetCell(tailStart + 2).NumericCellValue);   // 休息天数
        Assert.Equal(158.5, dataRow.GetCell(tailStart + 3).NumericCellValue);   // 正班工时
        Assert.Equal(5,     dataRow.GetCell(tailStart + 4).NumericCellValue);   // 迟到时长
        Assert.Equal(1,     dataRow.GetCell(tailStart + 5).NumericCellValue);   // 早退次数
        Assert.Equal(2,     dataRow.GetCell(tailStart + 6).NumericCellValue);   // 迟到次数
        Assert.Equal(3,     dataRow.GetCell(tailStart + 7).NumericCellValue);   // 早退时长
        Assert.Equal(1,     dataRow.GetCell(tailStart + 8).NumericCellValue);   // 上班缺卡次数
        Assert.Equal(1,     dataRow.GetCell(tailStart + 9).NumericCellValue);   // 下班缺卡次数
        Assert.Equal(1,     dataRow.GetCell(tailStart + 10).NumericCellValue);  // 旷工天数
        Assert.Equal(8,     dataRow.GetCell(tailStart + 11).NumericCellValue);  // 出差时长
        Assert.Equal(2,     dataRow.GetCell(tailStart + 12).NumericCellValue);  // 夜班次数
        Assert.Equal(16,    dataRow.GetCell(tailStart + 13).NumericCellValue);  // 夜班总工时
        Assert.Equal(10,    dataRow.GetCell(tailStart + 14).NumericCellValue);  // 加班总时长
        Assert.Equal(4,     dataRow.GetCell(tailStart + 15).NumericCellValue);  // 工作日加班
        Assert.Equal(3,     dataRow.GetCell(tailStart + 16).NumericCellValue);  // 休息日加班
        Assert.Equal(6,     dataRow.GetCell(tailStart + 17).NumericCellValue);  // 节假日加班
        Assert.Equal(22,    dataRow.GetCell(tailStart + 18).NumericCellValue);  // 应出勤天数（新增，放在最后一列）

        // 数据行最后一个有值的单元格必须正好是最后一列，不多出、也不少一列——
        // 这一条能直接抓出"表头 N 列，但写值那边多写/少写了一列"这种整体错位的 bug
        Assert.Equal(tailStart + ExpectedTailHeaders.Length, dataRow.LastCellNum);
    }

    [Fact]
    public void 周末列头保留日号_平日仍是纯数字_跨月不迷糊()
    {
        // 2026-08-28(五) 29(六) 30(日) 31(一) 09-01(二)
        var dates = new List<DateOnly> { new(2026, 8, 28), new(2026, 8, 29), new(2026, 8, 30), new(2026, 8, 31), new(2026, 9, 1) };
        var row = FullRow("张三", "E001");
        row.DailyHours = [null, null, null, null, null];
        row.DailyIsNightShift = [false, false, false, false, false];
        var sheet = Export(dates, row);
        var dayHeader = sheet.GetRow(3);

        var labels = Enumerable.Range(0, 5).Select(i => dayHeader.GetCell(6 + i).StringCellValue).ToArray();
        Assert.Equal(new[] { "28", "29六", "30日", "31", "1" }, labels);
    }

    [Fact]
    public void 有筛选箭头_范围从表头行到最后一个员工_不含合计行()
    {
        var dates = new List<DateOnly> { new(2026, 9, 1), new(2026, 9, 2) };
        var sheet = Export(dates, FullRow("甲", "E1"), FullRow("乙", "E2"), FullRow("丙", "E3"));

        var filter = ((XSSFSheet)sheet).GetCTWorksheet().autoFilter;
        Assert.NotNull(filter);
        // 表头在 Excel 第 4 行；3 个员工在第 5～7 行；合计行是第 8 行，不能被框进筛选范围
        Assert.Equal("A4:" + NPOI.SS.Util.CellReference.ConvertNumToColString(6 + 2 + ExpectedTailHeaders.Length - 1) + "7", filter.@ref);
    }

    [Fact]
    public void 合计行_每列求和_出勤天数是天数之和不是人数_并带上计算好的缓存值()
    {
        var dates = new List<DateOnly> { new(2026, 9, 1), new(2026, 9, 2) };
        var a = FullRow("甲", "E1", dayHours: 8);
        var b = FullRow("乙", "E2", dayHours: 9.5m);
        var sheet = Export(dates, a, b);

        var total = sheet.GetRow(6);   // 数据在第 4、5 行，合计紧跟其后
        Assert.Equal("合计", total.GetCell(0).StringCellValue);

        var tailStart = 6 + dates.Count;
        Assert.Equal(17.5, total.GetCell(6).NumericCellValue);                       // 9/1 当天工时合计
        Assert.Equal(42,   total.GetCell(tailStart + 0).NumericCellValue);           // 出勤天数：21 + 21
        Assert.Equal(317,  total.GetCell(tailStart + 3).NumericCellValue);           // 正班工时：158.5 × 2
        Assert.Equal(10,   total.GetCell(tailStart + 4).NumericCellValue);           // 迟到时长(分)
        Assert.Equal(2,    total.GetCell(tailStart + 10).NumericCellValue);          // 旷工天数
        Assert.Equal(44,   total.GetCell(tailStart + 18).NumericCellValue);          // 应出勤天数：22 + 22

        // 用 SUBTOTAL(109,…)：HR 筛选后合计只统计可见行
        Assert.Contains("SUBTOTAL(109,", total.GetCell(tailStart).CellFormula);
    }

    [Fact]
    public void 说明行写在原来的第2行_不额外插入新行_表头和数据行号不变()
    {
        var dates = new List<DateOnly> { new(2026, 9, 1), new(2026, 9, 2) };
        var sheet = Export(dates, FullRow("张三", "E001"));

        var legend = sheet.GetRow(1).GetCell(0).StringCellValue;
        Assert.Contains("报表生成时间", legend);
        Assert.Contains("黄底=夜班", legend);
        Assert.Contains("红色=缺卡/旷工", legend);
        Assert.Contains("带(分)的列单位是分钟", legend);

        Assert.Equal("姓名", sheet.GetRow(2).GetCell(0).StringCellValue);        // 主表头仍在第 3 行（下标 2）
        Assert.Equal("张三", sheet.GetRow(4).GetCell(0).StringCellValue);         // 第一个员工仍在下标 4
    }

    [Fact]
    public void 休息或节假日且没有工时的格子是浅灰底_有工时的休息日和应出勤但空白的格子不是()
    {
        var dates = new List<DateOnly> { new(2026, 8, 28), new(2026, 8, 29), new(2026, 8, 30), new(2026, 8, 31) };
        var row = FullRow("张三", "E001");
        row.DailyHours        = [8m, null, 4m, null];              // 周五上班；周六休息没工时；周日休息但加班了 4 小时；周一应出勤但空白
        row.DailyIsNightShift = [false, false, false, false];
        row.DailyIsRest       = [false, true, true, false];
        var sheet = Export(dates, row);
        var dataRow = sheet.GetRow(4);

        static short Fill(ICell c) => c.CellStyle.FillForegroundColor;
        var white = Fill(dataRow.GetCell(6));                       // 普通格子的底色
        Assert.NotEqual(white, Fill(dataRow.GetCell(7)));           // 休息且没工时 → 灰
        Assert.Equal(white,    Fill(dataRow.GetCell(8)));           // 休息日但有工时(加班) → 不标灰，直接看数字
        Assert.Equal(white,    Fill(dataRow.GetCell(9)));           // 应出勤但空白 → 保持纯空白，不标灰
        Assert.Equal(CellType.Blank, dataRow.GetCell(9).CellType);
    }

    [Fact]
    public void 固定列表头纵向合并两行_保证第4行是完整的一整行表头()
    {
        var dates = new List<DateOnly> { new(2026, 9, 1), new(2026, 9, 2) };
        var sheet = Export(dates, FullRow("张三", "E001"));

        var merged = Enumerable.Range(0, sheet.NumMergedRegions).Select(i => sheet.GetMergedRegion(i)).ToList();
        Assert.Contains(merged, m => m.FirstRow == 2 && m.LastRow == 3 && m.FirstColumn == 0 && m.LastColumn == 0);   // "姓名"
        var lastCol = 6 + dates.Count + ExpectedTailHeaders.Length - 1;
        Assert.Contains(merged, m => m.FirstRow == 2 && m.LastRow == 3 && m.FirstColumn == lastCol && m.LastColumn == lastCol);   // "应出勤天数"
    }
}
