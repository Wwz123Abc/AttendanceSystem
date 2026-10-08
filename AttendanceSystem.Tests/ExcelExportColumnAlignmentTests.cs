using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

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
        "加班总时长(h)", "工作日加班(h)", "休息日加班(h)",
        "应出勤天数", "实际总工时(h)", "排班说明"
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
        PayableHours          = 168.5m,
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
        var headerRow = sheet.GetRow(3);   // 第 4 行（下标 3）：列名行（含尾部统计列表头；单位/长名字里加了换行，比对前去掉）
        var dataRow   = sheet.GetRow(4);   // 第 4 行：第一个员工的数据行

        const int fixedCols = 6;
        var tailStart = fixedCols + dates.Count;

        for (var i = 0; i < ExpectedTailHeaders.Length; i++)
            Assert.Equal(ExpectedTailHeaders[i], headerRow.GetCell(tailStart + i).StringCellValue.Replace("\n", ""));

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
        Assert.Equal(22,    dataRow.GetCell(tailStart + 17).NumericCellValue);  // 应出勤天数
        Assert.Equal(168.5, dataRow.GetCell(tailStart + 18).NumericCellValue);  // 实际总工时（正班+加班，发工资按这个）

        // 数据行最后一个有值的单元格必须正好是最后一列，不多出、也不少一列——
        // 这一条能直接抓出"表头 N 列，但写值那边多写/少写了一列"这种整体错位的 bug
        Assert.Equal(tailStart + ExpectedTailHeaders.Length, dataRow.LastCellNum);
    }

    [Fact]
    public void 日期列头每天都是日号加星期_跨月不迷糊()
    {
        // 2026-08-28(五) 29(六) 30(日) 31(一) 09-01(二)
        var dates = new List<DateOnly> { new(2026, 8, 28), new(2026, 8, 29), new(2026, 8, 30), new(2026, 8, 31), new(2026, 9, 1) };
        var row = FullRow("张三", "E001");
        row.DailyHours = [null, null, null, null, null];
        row.DailyIsNightShift = [false, false, false, false, false];
        var sheet = Export(dates, row);
        var dayHeader = sheet.GetRow(3);

        var labels = Enumerable.Range(0, 5).Select(i => dayHeader.GetCell(6 + i).StringCellValue).ToArray();
        Assert.Equal(new[] { "28\n五", "29\n六", "30\n日", "31\n一", "1\n二" }, labels);   // 每天都写"日号↵星期"
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
        Assert.StartsWith("合计", total.GetCell(0).StringCellValue);

        var tailStart = 6 + dates.Count;
        Assert.Equal(17.5, total.GetCell(6).NumericCellValue);                       // 9/1 当天工时合计
        Assert.Equal(42,   total.GetCell(tailStart + 0).NumericCellValue);           // 出勤天数：21 + 21
        Assert.Equal(317,  total.GetCell(tailStart + 3).NumericCellValue);           // 正班工时：158.5 × 2
        Assert.Equal(10,   total.GetCell(tailStart + 4).NumericCellValue);           // 迟到时长(分)
        Assert.Equal(2,    total.GetCell(tailStart + 10).NumericCellValue);          // 旷工天数
        Assert.Equal(44,   total.GetCell(tailStart + 17).NumericCellValue);          // 应出勤天数：22 + 22

        // 用 SUBTOTAL(109,…)：HR 筛选后合计只统计可见行
        Assert.Contains("SUBTOTAL(109,", total.GetCell(tailStart).CellFormula);
    }

    [Fact]
    public void 说明行写在原来的第2行_不额外插入新行_表头和数据行号不变()
    {
        // 天数太少放不下图例块时，退回成一行短说明（内容仍然完整）
        var dates = new List<DateOnly> { new(2026, 9, 1), new(2026, 9, 2) };
        var sheet = Export(dates, FullRow("张三", "E001"));

        var legend = sheet.GetRow(1).GetCell(0).StringCellValue;
        Assert.Contains("报表生成时间", legend);
        Assert.Contains("夜班", legend);
        Assert.Contains("缺卡/旷工", legend);
        Assert.Contains("带(分)的列单位是分钟", legend);

        Assert.Equal("姓名", sheet.GetRow(3).GetCell(0).StringCellValue);        // 列名行在第 4 行（下标 3）
        Assert.Equal("张三", sheet.GetRow(4).GetCell(0).StringCellValue);         // 第一个员工仍在下标 4（数据从第 5 行起，没变）
    }

    private static List<DateOnly> Days(int n) => Enumerable.Range(0, n).Select(i => new DateOnly(2026, 8, 26).AddDays(i)).ToList();

    private static TemplateReportRowDto RowFor(int dayCount, string name = "张三", string no = "E001")
    {
        var r = FullRow(name, no);
        r.DailyHours = Enumerable.Repeat<decimal?>(null, dayCount).ToList();
        r.DailyIsNightShift = Enumerable.Repeat(false, dayCount).ToList();
        r.DailyIsRest = Enumerable.Repeat(false, dayCount).ToList();
        return r;
    }

    private static string? FillHex(ICell c) => ((XSSFCellStyle)c.CellStyle).FillForegroundXSSFColor?.ARGBHex;
    private static string? FontHex(ICell c) => ((XSSFCellStyle)c.CellStyle).GetFont().GetXSSFColor()?.ARGBHex;

    [Fact]
    public void 标题靠左_主标题加统计周期和人数_第1行不合并()
    {
        var dates = Days(31);
        var sheet = Export(dates, RowFor(31, "甲", "E1"), RowFor(31, "乙", "E2"));

        Assert.Equal("发薪考勤汇总", sheet.GetRow(0).GetCell(0).StringCellValue);
        var sub = sheet.GetRow(0).GetCell(2).StringCellValue;
        Assert.Contains("统计周期 2026-08-26 至 2026-09-25", sub);
        Assert.Contains("31 天", sub);
        Assert.Contains("共 2 人", sub);
        Assert.Equal(HorizontalAlignment.Left, sheet.GetRow(0).GetCell(0).CellStyle.Alignment);
        // 第 1 行不再合并成一整行居中（打开冻结窗格后标题会跑到很右边看不见）
        Assert.DoesNotContain(Enumerable.Range(0, sheet.NumMergedRegions).Select(i => sheet.GetMergedRegion(i)), m => m.FirstRow == 0);
    }

    [Fact]
    public void 天数够多时第2行是彩色图例块_颜色和表格里的一致()
    {
        var dates = Days(31);
        var sheet = Export(dates, RowFor(31));
        var legend = sheet.GetRow(1);

        Assert.Contains("报表生成时间", legend.GetCell(0).StringCellValue);
        Assert.Equal("夜班", legend.GetCell(6).StringCellValue);
        Assert.Equal("休息/节假日", legend.GetCell(8).StringCellValue);
        Assert.Equal("迟到/早退", legend.GetCell(11).StringCellValue);
        Assert.Equal("缺卡/旷工", legend.GetCell(14).StringCellValue);
        Assert.Equal("FFFFF2CC", FillHex(legend.GetCell(6)));       // 淡黄：和表格里夜班格子同色
        Assert.Equal("FFF2F2F2", FillHex(legend.GetCell(8)));       // 浅灰：和休息日格子同色
        Assert.Equal("FFC55A11", FontHex(legend.GetCell(11)));      // 深橙字：和迟到/早退数字同色
        Assert.Equal("FFC00000", FontHex(legend.GetCell(14)));      // 深红字：和缺卡/旷工数字同色
        Assert.Contains("当日工时", legend.GetCell(17).StringCellValue);
        Assert.Contains("(分) = 分钟", legend.GetCell(6 + 31).StringCellValue);     // 统计列区域的单位说明
    }

    [Fact]
    public void 分组行加列名行两层表头_每一列都有列名_筛选下拉框不再空白()
    {
        var dates = Days(31);
        var sheet = Export(dates, RowFor(31));
        var totalCols = 6 + 31 + ExpectedTailHeaders.Length;

        // 第 4 行（筛选箭头所在行）每一列都有列名——筛选下拉框里每列都有标题（第 10 轮清单 #3 的修复）
        for (var c = 0; c < totalCols; c++)
            Assert.False(string.IsNullOrWhiteSpace(sheet.GetRow(3).GetCell(c)?.StringCellValue), $"第 {c + 1} 列的列名是空的");

        // 第 3 行是分组行，按 6 组合并
        var band = sheet.GetRow(2);
        string[] names = ["基本信息", "考勤结果（每日工时，单位：小时）", "出勤与工时", "迟到 · 早退 · 缺卡 · 旷工", "出差 · 夜班 · 加班", "对照", "发薪工时（正班+加班）", "说明"];
        int[] firsts = [0, 6, 37, 41, 48, 54, 55, 56];
        for (var i = 0; i < names.Length; i++) Assert.Equal(names[i], band.GetCell(firsts[i]).StringCellValue);
        var merged = Enumerable.Range(0, sheet.NumMergedRegions).Select(i => sheet.GetMergedRegion(i)).ToList();
        Assert.Contains(merged, m => m.FirstRow == 2 && m.LastRow == 2 && m.FirstColumn == 0 && m.LastColumn == 5);
        Assert.Contains(merged, m => m.FirstRow == 2 && m.LastRow == 2 && m.FirstColumn == 6 && m.LastColumn == 36);
        Assert.Contains(merged, m => m.FirstRow == 2 && m.LastRow == 2 && m.FirstColumn == 37 && m.LastColumn == 40);
        Assert.Contains(merged, m => m.FirstRow == 2 && m.LastRow == 2 && m.FirstColumn == 41 && m.LastColumn == 47);
        Assert.Contains(merged, m => m.FirstRow == 2 && m.LastRow == 2 && m.FirstColumn == 48 && m.LastColumn == 53);
        // 不再有纵向合并（合并会让筛选行上的那格变成空白）
        Assert.DoesNotContain(merged, m => m.FirstRow == 2 && m.LastRow == 3);
        Assert.Equal(6 + 31 + ExpectedTailHeaders.Length, band.LastCellNum);   // 分组行总列数没变
        Assert.Equal(HorizontalAlignment.Left, band.GetCell(6).CellStyle.Alignment);   // "考勤结果"靠左，冻结后往右滚动仍能看到
    }

    [Fact]
    public void 日期表头周末灰字灰底_工作日深蓝字()
    {
        var dates = Days(31);   // 8/26(三) 8/27(四) 8/28(五) 8/29(六) 8/30(日)
        var sheet = Export(dates, RowFor(31));
        var h = sheet.GetRow(3);
        Assert.Equal("FFEEF3FA", FillHex(h.GetCell(6)));
        Assert.Equal("FF1F3864", FontHex(h.GetCell(6)));
        Assert.Equal("FFE7E6E6", FillHex(h.GetCell(9)));            // 8/29 周六
        Assert.Equal("FF808080", FontHex(h.GetCell(9)));
        Assert.Equal("FFE7E6E6", FillHex(h.GetCell(10)));           // 8/30 周日
    }

    [Fact]
    public void 数字格式_工时列一位小数零显示横线_分钟列千分位_每日格子里的0是浅灰字()
    {
        var dates = Days(2);
        var row = FullRow("张三", "E001");
        row.DailyHours = [0m, 8m];
        row.DailyIsNightShift = [false, false];
        row.LateMinutes = 1726;
        var sheet = Export(dates, row);
        var data = sheet.GetRow(4);
        var tailStart = 6 + 2;

        Assert.Equal("0.0;-0.0;\"-\"", data.GetCell(tailStart + 3).CellStyle.GetDataFormatString());    // 正班工时(h)
        Assert.Equal("0.0;-0.0;\"-\"", data.GetCell(tailStart + 11).CellStyle.GetDataFormatString());   // 出差时长(h)
        Assert.Equal("#,##0", data.GetCell(tailStart + 4).CellStyle.GetDataFormatString());              // 迟到时长(分)
        Assert.Equal("General", data.GetCell(tailStart + 0).CellStyle.GetDataFormatString());            // 出勤天数：常规
        Assert.Equal("FFBFBFBF", FontHex(data.GetCell(6)));         // 当天工时 0：浅灰字
        Assert.Equal("FF262626", FontHex(data.GetCell(7)));         // 有工时：深灰字
        Assert.Equal("FFC55A11", FontHex(data.GetCell(tailStart + 4)));    // 迟到：深橙
        Assert.Equal("FFC00000", FontHex(data.GetCell(tailStart + 10)));   // 旷工：深红
    }

    [Fact]
    public void 文字列靠左数字居中_长文字不缩小字体()
    {
        var dates = Days(2);
        var sheet = Export(dates, FullRow("张三", "E001"));
        var data = sheet.GetRow(4);
        foreach (var col in new[] { 0, 1, 2, 4, 5 })
        {
            Assert.Equal(HorizontalAlignment.Left, data.GetCell(col).CellStyle.Alignment);
            Assert.Equal(1, data.GetCell(col).CellStyle.Indention);
            Assert.False(data.GetCell(col).CellStyle.ShrinkToFit);
        }
        Assert.Equal(HorizontalAlignment.Center, data.GetCell(3).CellStyle.Alignment);   // 工号居中
        Assert.Equal(HorizontalAlignment.Center, data.GetCell(6).CellStyle.Alignment);   // 每日格子居中
    }

    [Fact]
    public void 列宽紧凑_冻结前6列4行_A3横向打印带页码和打印日期()
    {
        var dates = Days(2);
        var sheet = Export(dates, FullRow("张三", "E001"));

        Assert.Equal(9 * 256,   sheet.GetColumnWidth(0));
        Assert.Equal(13 * 256,  sheet.GetColumnWidth(1));
        Assert.Equal(15 * 256,  sheet.GetColumnWidth(5));
        Assert.Equal((int)(4.8 * 256), sheet.GetColumnWidth(6));            // 每日列
        Assert.Equal((int)(8.2 * 256), sheet.GetColumnWidth(6 + 2));        // 统计列
        Assert.Equal(6, sheet.PaneInformation.VerticalSplitPosition);        // 冻结前 6 列
        Assert.Equal(4, sheet.PaneInformation.HorizontalSplitPosition);      // 冻结前 4 行

        Assert.Equal(8, sheet.PrintSetup.PaperSize);                         // 8 = A3
        Assert.True(sheet.PrintSetup.Landscape);
        Assert.True(sheet.HorizontallyCenter);
        Assert.Equal("第 &P 页 / 共 &N 页", sheet.Footer.Center);
        Assert.Equal("打印日期 &D", sheet.Footer.Right);
    }

    [Fact]
    public void 合计行_合并前6列_换配色_每日合计缩小字号防止显示井号()
    {
        var dates = Days(2);
        var sheet = Export(dates, FullRow("甲", "E1"), FullRow("乙", "E2"));
        var total = sheet.GetRow(6);

        Assert.Equal("合计（随筛选结果变化）", total.GetCell(0).StringCellValue);
        var merged = Enumerable.Range(0, sheet.NumMergedRegions).Select(i => sheet.GetMergedRegion(i)).ToList();
        Assert.Contains(merged, m => m.FirstRow == 6 && m.LastRow == 6 && m.FirstColumn == 0 && m.LastColumn == 5);
        Assert.Equal("FFD6DCE4", FillHex(total.GetCell(0)));
        Assert.True(total.GetCell(6).CellStyle.ShrinkToFit);                // 每日合计：放不下自动缩小
        Assert.Equal("#,##0.0;-#,##0.0;\"-\"", total.GetCell(8 + 3).CellStyle.GetDataFormatString());   // 工时类合计
        Assert.Equal("#,##0;-#,##0;\"-\"", total.GetCell(8 + 4).CellStyle.GetDataFormatString());       // 分钟类合计
        Assert.Contains("SUBTOTAL(109,", total.GetCell(8).CellFormula);
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

        Assert.Null(FillHex(dataRow.GetCell(6)));                   // 普通格子：白底（不填色）
        Assert.Equal("FFF2F2F2", FillHex(dataRow.GetCell(7)));      // 休息且没工时 → 浅灰
        Assert.Null(FillHex(dataRow.GetCell(8)));                   // 休息日但有工时(加班) → 不标灰，直接看数字
        Assert.Null(FillHex(dataRow.GetCell(9)));                   // 应出勤但空白 → 保持纯空白，不标灰
        Assert.Equal(CellType.Blank, dataRow.GetCell(9).CellType);
    }

    [Fact]
    public void 夜班当天的格子是淡黄底_包括没有工时的格子()
    {
        var dates = new List<DateOnly> { new(2026, 8, 28), new(2026, 8, 29) };
        var row = FullRow("张三", "E001");
        row.DailyHours        = [8m, null];
        row.DailyIsNightShift = [true, true];
        var sheet = Export(dates, row);
        Assert.Equal("FFFFF2CC", FillHex(sheet.GetRow(4).GetCell(6)));
        Assert.Equal("FFFFF2CC", FillHex(sheet.GetRow(4).GetCell(7)));   // 没打卡的夜班格子也标黄，才能连成一块
    }

    [Fact]
    public void 列顺序和行号没变_新增美化没有挪动任何列或行()
    {
        // 发工资那边如果有程序按位置读这份表：标题第 1 行、图例第 2 行、分组第 3 行、列名第 4 行、数据从第 5 行起、合计紧跟最后一个员工
        var dates = new List<DateOnly> { new(2026, 9, 1), new(2026, 9, 2) };
        var sheet = Export(dates, FullRow("甲", "E1"), FullRow("乙", "E2"));
        Assert.Equal(new[] { "姓名", "考勤组", "部门", "工号", "职位", "合同公司" },
            Enumerable.Range(0, 6).Select(i => sheet.GetRow(3).GetCell(i).StringCellValue).ToArray());
        Assert.Equal("甲", sheet.GetRow(4).GetCell(0).StringCellValue);
        Assert.Equal("乙", sheet.GetRow(5).GetCell(0).StringCellValue);
        Assert.StartsWith("合计", sheet.GetRow(6).GetCell(0).StringCellValue);
        Assert.Equal(6, sheet.LastRowNum);
    }
}
