using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.SS.Util;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;

namespace AttendanceSystem.Helpers;

// 本文件用 NPOI 这个库，在内存里“拼”出一个 Excel(.xlsx) 文件，最后返回字节数组供浏览器下载。
// 几个概念：Workbook=整个 Excel 文件；Sheet=一个工作表(底部标签页)；Row=一行；Cell=一个单元格；Style=单元格样式(字体/颜色/边框)。

/// <summary>Excel 报表导出工具。</summary>
public static class ExcelExportHelper
{
    // ── 报表 1：月度考勤汇总表（一行一个人，最后一行合计）─────────────────────────
    public static byte[] ExportMonthlySummary(
        List<MonthlySummaryDto> summaries, int year, int month)
    {
        using var wb = new XSSFWorkbook();                            // 新建一个 Excel 文件
        var sheet  = wb.CreateSheet($"{year}年{month:D2}月考勤汇总");  // 新建一个工作表

        // 预先准备好几种单元格样式，后面反复用
        var titleStyle     = TitleStyle(wb);   // 大标题
        var headerStyle    = HeaderStyle(wb);  // 表头（灰底加粗）
        var dataStyle      = DataStyle(wb);    // 普通数据
        var hourStyle      = HourStyle(wb);        // 工时类小数（固定 2 位小数）
        var bandedStyle    = BandedStyle(wb);      // 隔行浅灰底
        var bandedHourStyle = BandedHourStyle(wb); // 隔行浅灰底 + 固定 2 位小数
        var redStyle    = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Red.Index);     // 红字（旷工/缺卡）
        var orangeStyle = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Orange.Index);  // 橙字（迟到/早退）

        // 第 0 行：大标题，并把前 14 列合并成一格
        var titleRow = sheet.CreateRow(0);
        SetCell(titleRow, 0, $"{year}年{month:D2}月员工考勤汇总表", titleStyle);
        sheet.AddMergedRegion(new CellRangeAddress(0, 0, 0, 14));
        titleRow.HeightInPoints = 28;

        // 第 1 行：表头（每一列的名字）
        string[] headers =
        [
            "工号", "姓名", "部门", "岗位",
            "应出勤天数", "实际出勤天数", "夜班天数",
            "迟到次数", "早退次数", "旷工天数", "未打卡次数", "请假天数",
            "加班工时(h)", "实际工时(h)", "审批通过次数"
        ];
        var headerRow = sheet.CreateRow(1);
        headerRow.HeightInPoints = 20;
        for (var i = 0; i < headers.Length; i++)
        {
            SetCell(headerRow, i, headers[i], headerStyle);
            sheet.SetColumnWidth(i, 14 * 256);   // 设置列宽（NPOI 里 1 个字符宽 = 256）
        }
        sheet.SetAutoFilter(new CellRangeAddress(1, 1, 0, headers.Length - 1));   // 表头加筛选箭头，方便按列筛选/排序
        ApplyLookAndFeel(sheet, freezeCols: 4, freezeRows: 2, repeatHeaderRows: 2);   // 冻结"工号/姓名/部门/岗位"这几列+标题表头

        // 从第 2 行起：每个员工一行数据（异常的数字用红/橙色突出；隔行浅灰底，方便对齐看清一整行）
        for (var r = 0; r < summaries.Count; r++)
        {
            var dto = summaries[r];
            var row = sheet.CreateRow(r + 2);
            var baseStyle = r % 2 == 1 ? bandedStyle : dataStyle;
            var hStyle    = r % 2 == 1 ? bandedHourStyle : hourStyle;
            SetCell(row, 0,  dto.EmployeeNo,                              baseStyle);
            SetCell(row, 1,  dto.RealName,                                baseStyle);
            SetCell(row, 2,  dto.DeptName ?? "",                          baseStyle);
            SetCell(row, 3,  dto.Position ?? "",                          baseStyle);
            SetCell(row, 4,  dto.ExpectedWorkdays,                        baseStyle);
            SetCell(row, 5,  (double)dto.ActualWorkdays,                  baseStyle);
            SetCell(row, 6,  dto.NightShiftDays,                          baseStyle);   // 夜班天数
            SetCell(row, 7,  dto.LateCount,        dto.LateCount > 0        ? orangeStyle : baseStyle);  // 有迟到→橙
            SetCell(row, 8,  dto.EarlyLeaveCount,  dto.EarlyLeaveCount > 0  ? orangeStyle : baseStyle);  // 有早退→橙
            SetCell(row, 9,  dto.AbsentDays,       dto.AbsentDays > 0       ? redStyle    : baseStyle);  // 有旷工→红
            SetCell(row, 10, dto.NotPunchedCount,  dto.NotPunchedCount > 0  ? redStyle    : baseStyle);  // 有缺卡→红
            SetCell(row, 11, (double)dto.LeaveDays,                       hStyle);
            SetCell(row, 12, (double)dto.TotalOvertimeHours,              hStyle);
            SetCell(row, 13, (double)dto.TotalWorkHours,                  hStyle);
            SetCell(row, 14, dto.ApprovedCount,                           baseStyle);
        }

        // 最后一行：各列合计
        if (summaries.Count > 0)
        {
            var totalRow = sheet.CreateRow(summaries.Count + 2);
            totalRow.HeightInPoints = 18;
            SetCell(totalRow, 0,  "合计",                                                     headerStyle);
            SetCell(totalRow, 4,  summaries.Sum(s => s.ExpectedWorkdays),                    headerStyle);
            SetCell(totalRow, 5,  (double)summaries.Sum(s => s.ActualWorkdays),              headerStyle);
            SetCell(totalRow, 6,  summaries.Sum(s => s.NightShiftDays),                      headerStyle);
            SetCell(totalRow, 7,  summaries.Sum(s => s.LateCount),                           headerStyle);
            SetCell(totalRow, 8,  summaries.Sum(s => s.EarlyLeaveCount),                     headerStyle);
            SetCell(totalRow, 9,  summaries.Sum(s => s.AbsentDays),                          headerStyle);
            SetCell(totalRow, 10, summaries.Sum(s => s.NotPunchedCount),                     headerStyle);
            SetCell(totalRow, 11, (double)summaries.Sum(s => s.LeaveDays),                   headerStyle);
            SetCell(totalRow, 12, (double)summaries.Sum(s => s.TotalOvertimeHours),          headerStyle);
            SetCell(totalRow, 13, (double)summaries.Sum(s => s.TotalWorkHours),              headerStyle);
            SetCell(totalRow, 14, summaries.Sum(s => s.ApprovedCount),                       headerStyle);
        }

        return ToBytes(wb);   // 把 Excel 转成字节数组返回（供下载）
    }

    // ── 报表 2：个人每日考勤明细（一行一天）─────────────────────────────────────
    public static byte[] ExportDailyStatusReport(MonthlySummaryDto summary)
    {
        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet($"{summary.RealName}_{summary.Year}年{summary.Month:D2}月");

        var headerStyle = HeaderStyle(wb);
        var dataStyle   = DataStyle(wb);
        var bandedStyle = BandedStyle(wb);   // 隔行浅色底
        var redStyle    = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Red.Index);
        var orangeStyle = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Orange.Index);
        var grayStyle   = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Grey50Percent.Index);

        // 标题（前 9 列合并）
        var titleRow = sheet.CreateRow(0);
        SetCell(titleRow, 0,
            $"{summary.RealName}（{summary.EmployeeNo}）{summary.Year}年{summary.Month:D2}月每日考勤明细",
            TitleStyle(wb));
        sheet.AddMergedRegion(new CellRangeAddress(0, 0, 0, 8));
        titleRow.HeightInPoints = 28;

        // 表头
        string[] headers = ["日期", "星期", "上班打卡", "下班打卡", "考勤状态", "实际工时(h)", "加班(h)", "迟到(分)", "备注/审批"];
        var headerRow = sheet.CreateRow(1);
        headerRow.HeightInPoints = 20;
        for (var i = 0; i < headers.Length; i++)
        {
            SetCell(headerRow, i, headers[i], headerStyle);
            sheet.SetColumnWidth(i, i == 8 ? 22 * 256 : 14 * 256);   // 最后一列(备注)宽一点
        }
        ApplyLookAndFeel(sheet, freezeCols: 2, freezeRows: 2, repeatHeaderRows: 2);   // 冻结"日期/星期"这两列+标题表头

        // 每天一行；按考勤状态给"状态"单元格上色；隔行浅色底，方便对齐看清一整行
        for (var i = 0; i < summary.DailyRecords.Count; i++)
        {
            var rec = summary.DailyRecords[i];
            var row = sheet.CreateRow(i + 2);
            var baseStyle = i % 2 == 1 ? bandedStyle : dataStyle;

            // 旷工/缺卡→红；迟到/早退→橙；节假日→灰（这个"灰"分支目前实际上不会触发，
            // 因为 rec.IsHoliday 这个值在整个系统里从来没被真正设置过 true，一直是 false；
            // 保留这段判断是为了以后万一把 IsHoliday 接上了，颜色逻辑不用再改）；正常→隔行浅色底
            var statusStyle = rec.AttendanceStatus is AttendanceStatus.Absent or AttendanceStatus.NotPunched
                ? redStyle
                : rec.AttendanceStatus is AttendanceStatus.Late or AttendanceStatus.EarlyLeave
                ? orangeStyle
                : rec.IsHoliday
                ? grayStyle
                : baseStyle;

            SetCell(row, 0, rec.WorkDateText,                baseStyle);
            SetCell(row, 1, rec.DayOfWeekText,               baseStyle);
            SetCell(row, 2, rec.ClockInText,                 baseStyle);
            SetCell(row, 3, rec.ClockOutText,                baseStyle);
            SetCell(row, 4, rec.StatusText,                  statusStyle);
            // 工时/加班按"半小时"为最小单位展示（不足半小时舍去），和月度汇总的合计口径一致
            SetCell(row, 5, (double)AttendanceService.FloorToHalf(rec.ActualWorkHours),  baseStyle);
            SetCell(row, 6, (double)AttendanceService.FloorToHalf(rec.OvertimeHours),    baseStyle);
            SetCell(row, 7, rec.LateMinutes,
                rec.LateMinutes > 0 ? orangeStyle : baseStyle);
            SetCell(row, 8, rec.ApprovalNote ?? "",          baseStyle);
        }

        return ToBytes(wb);
    }

    // ── 报表 3：模板月度汇总表（对照公司要求的外部模板文件列结构，一行一个人，含每日打卡格子）──
    // 2026-09-28 按《月度汇总导出表_美化优化建议》做了美化：标题靠左、图例块、"分组行 + 列名行"两层表头、每天带星期、
    // 浅灰格线 + 分组分隔线、柔和配色、文字靠左数字居中、统一数字格式、更紧凑的列宽、合计行换配色、A3 打印。
    // ★ 硬约束（发工资那边如果有程序按位置读这份表，不能错位）：列的顺序和位置不变（新列只能加在最后）；
    //   行号不变：标题第 1 行、图例第 2 行、分组行第 3 行、列名行第 4 行、数据从第 5 行起、合计行在最后；
    //   合计仍是 SUBTOTAL(109,…)，筛选范围仍是"列名行到最后一个员工"。
    public static byte[] ExportTemplateReport(TemplateReportResultDto result)
    {
        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("月度汇总");
        var st = new TemplateReportStyles(wb);   // 样式全部提前按需建好并缓存、循环里复用（最多 2000+ 行，不能每格新建样式对象）

        var dayCount  = result.Dates.Count;
        var fixedCols = 6;                       // 姓名/考勤组/部门/工号/职位/合同公司
        // 尾部统计列。表头带单位：迟到/早退时长是"分钟"，正班工时/出差/夜班/加班这几列是"小时"，
        // 同一张表里两种单位混着，不写单位很容易把 752（分钟）当成小时去算工资。
        // ★ 新增的列只能加在最后（下面"应出勤天数"），不能插在中间——下游如果有程序/对照表按列位置取数，会整体错位。
        // ★ 以后在最后加列，要同步扩大下面 groups 里最后一组（对照）的范围。
        string[] tailHeaders =
        [
            "出勤天数", "请假天数", "休息天数", "正班工时(h)", "迟到时长(分)", "早退次数", "迟到次数", "早退时长(分)",
            "上班缺卡次数", "下班缺卡次数", "旷工天数", "出差时长(h)", "夜班次数", "夜班总工时(h)",
            "加班总时长(h)", "工作日加班(h)", "休息日加班(h)",
            "应出勤天数"
        ];
        var tailCols   = tailHeaders.Length;      // 直接取表头个数，不再手写数字
        var totalCols  = fixedCols + dayCount + tailCols;
        var tailStart  = fixedCols + dayCount;
        var lastDayCol = tailStart - 1;
        // 分组的最后一列：右边线用深一点的颜色当分组分隔线（合同公司、每日最后一天、正班工时、旷工天数、休息日加班）
        var edgeCols = new HashSet<int> { fixedCols - 1, lastDayCol, tailStart + 3, tailStart + 10, tailStart + 16 };
        // 带单位的工时列（(h)）用 1 位小数、0 显示"-"；分钟列（(分)）用千分位
        var hourCols   = new HashSet<int> { tailStart + 3, tailStart + 11, tailStart + 13, tailStart + 14, tailStart + 15, tailStart + 16 };
        var minuteCols = new HashSet<int> { tailStart + 4, tailStart + 7 };

        // ── 第 1 行：标题靠左，拆成"主标题 + 统计周期/人数"两段（不合并，这样冻结窗格后一打开就能看到）──
        var titleRow = sheet.CreateRow(0);
        titleRow.HeightInPoints = 34;
        SetCell(titleRow, 0, "月度考勤汇总", st.Get(font: "1F3864", size: 16, bold: true, h: HorizontalAlignment.Left, border: false));
        SetCell(titleRow, 2,
            $"统计周期 {result.StartDate:yyyy-MM-dd} 至 {result.EndDate:yyyy-MM-dd}（{dayCount} 天）· 共 {result.Rows.Count} 人",
            st.Get(font: "7F7F7F", size: 11, h: HorizontalAlignment.Left, border: false));

        // ── 第 2 行：报表生成时间 + 彩色图例块 + 一句短说明（还是只占这一行，不额外插入新行）──
        var legendRow = sheet.CreateRow(1);
        legendRow.HeightInPoints = 22;
        var noteStyle = st.Get(font: "7F7F7F", size: 9, h: HorizontalAlignment.Left, border: false);
        var genText   = $"报表生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}";
        if (dayCount >= 14)
        {
            void Block(int first, int last, string text, ICellStyle style)
            {
                for (var c = first; c <= last; c++) SetCell(legendRow, c, c == first ? text : "", style);
                sheet.AddMergedRegion(new CellRangeAddress(1, 1, first, last));
            }
            Block(0, 5, genText, noteStyle);
            Block(6, 7, "夜班", st.Get(fill: "FFF2CC", size: 9));
            Block(8, 10, "休息/节假日", st.Get(fill: "F2F2F2", size: 9));
            Block(11, 13, "迟到/早退", st.Get(font: "C55A11", size: 9, bold: true));
            Block(14, 16, "缺卡/旷工", st.Get(font: "C00000", size: 9, bold: true));
            Block(17, lastDayCol, "格内数字 = 当日工时（小时，不足半小时舍去）；空白 = 应出勤但没有工时", noteStyle);
            Block(tailStart, totalCols - 1, "(分) = 分钟　(h) = 小时　最后一行“合计”会随筛选结果变化", noteStyle);
        }
        else
        {
            // 天数太少（比如只导出一周）放不下图例块：退回成一行短说明
            SetCell(legendRow, 0,
                genText + "　｜　夜班=淡黄底；休息日/节假日（当天没有工时）=浅灰底；迟到/早退=橙色字；缺卡/旷工=红色字；" +
                "格内数字=当日工时（小时，不足半小时舍去）；空白=应出勤但没有工时；带(分)的列单位是分钟，带(h)的列单位是小时；最后一行“合计”会随筛选结果变化。",
                st.Get(font: "7F7F7F", size: 9, h: HorizontalAlignment.Left, wrap: true, border: false));
            for (var c = 1; c < totalCols; c++) legendRow.CreateCell(c);
            sheet.AddMergedRegion(new CellRangeAddress(1, 1, 0, totalCols - 1));
            legendRow.HeightInPoints = 32;
        }

        // ── 第 3 行：分组行（深蓝底白字）；第 4 行：列名行（所有列名都写在这一行，筛选箭头也挂在这一行，
        //    所以筛选下拉框里每一列都有名字，不再有纵向合并的空白格）──
        var bandRow = sheet.CreateRow(2);
        var nameRow = sheet.CreateRow(3);
        bandRow.HeightInPoints = 20;
        nameRow.HeightInPoints = 34;
        (string Name, int First, int Last, string Tint, HorizontalAlignment Align)[] groups =
        [
            ("基本信息",                       0,              fixedCols - 1,  "D6DCE4", HorizontalAlignment.Center),
            // "考勤结果"横跨每日列，文字靠左（居中的话冻结窗格后往右滚动就看不到了）
            ("考勤结果（每日工时，单位：小时）", fixedCols,       lastDayCol,     "EEF3FA", HorizontalAlignment.Left),
            ("出勤与工时",                     tailStart,      tailStart + 3,  "DDEBF7", HorizontalAlignment.Center),
            ("迟到 · 早退 · 缺卡 · 旷工",       tailStart + 4,  tailStart + 10, "FBE5D6", HorizontalAlignment.Center),
            ("出差 · 夜班 · 加班",              tailStart + 11, tailStart + 16, "E2EFDA", HorizontalAlignment.Center),
            ("对照",                           tailStart + 17, tailStart + 17, "DDEBF7", HorizontalAlignment.Center),
        ];
        foreach (var g in groups)
        {
            for (var c = g.First; c <= g.Last; c++)   // 合并区里每个格子都要套样式，底色/边框才完整
                SetCell(bandRow, c, c == g.First ? g.Name : "",
                    st.Get(fill: "1F3864", font: "FFFFFF", size: 10, bold: true, h: g.Align, edge: c == g.Last));
            if (g.Last > g.First)   // 只有一列的分组不能合并（合并区至少要 2 个格子）
                sheet.AddMergedRegion(new CellRangeAddress(2, 2, g.First, g.Last));
        }
        string[] fixedHeaders = ["姓名", "考勤组", "部门", "工号", "职位", "合同公司"];
        for (var i = 0; i < fixedHeaders.Length; i++)
            SetCell(nameRow, i, fixedHeaders[i], st.Get(fill: "D6DCE4", font: "1F3864", size: 10, bold: true, edge: edgeCols.Contains(i), bottomMedium: true));

        // 每天的日期表头：每天都写"日号↵星期"，周末灰字灰底，一眼看出哪几天是周末
        const string weekNames = "日一二三四五六";   // DayOfWeek 周日=0
        for (var i = 0; i < dayCount; i++)
        {
            var date = result.Dates[i];
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            SetCell(nameRow, fixedCols + i, $"{date.Day}\n{weekNames[(int)date.DayOfWeek]}",
                st.Get(fill: weekend ? "E7E6E6" : "EEF3FA", font: weekend ? "808080" : "1F3864", size: 9, bold: true, wrap: true,
                       edge: edgeCols.Contains(fixedCols + i), bottomMedium: true));
        }
        // 统计列表头：单位换到第二行；5~6 个字没有单位的在中间换行（只是加了换行符，文字本身不变）
        static string WrapHeader(string h) =>
            h.Contains('(') ? h.Replace("(", "\n(")
            : h.Length >= 5 ? h.Insert(h.Length == 6 ? 4 : 3, "\n")
            : h;
        for (var i = 0; i < tailHeaders.Length; i++)
        {
            var col = tailStart + i;
            var tint = groups.First(g => col >= g.First && col <= g.Last).Tint;
            SetCell(nameRow, col, WrapHeader(tailHeaders[i]),
                st.Get(fill: tint, font: "1F3864", size: 9, bold: true, wrap: true, edge: edgeCols.Contains(col), bottomMedium: true));
        }

        // 列宽：文字列按内容给，每日列和统计列压窄（同一屏多看十来列）
        int[] fixedWidths = [9, 13, 11, 10, 10, 15];
        for (var i = 0; i < fixedCols; i++) sheet.SetColumnWidth(i, fixedWidths[i] * 256);
        for (var i = 0; i < dayCount; i++) sheet.SetColumnWidth(fixedCols + i, (int)(4.8 * 256));
        for (var i = 0; i < tailCols; i++) sheet.SetColumnWidth(tailStart + i, (int)(8.2 * 256));
        ApplyLookAndFeel(sheet, freezeCols: fixedCols, freezeRows: 4, repeatHeaderRows: 4);   // 冻结前 6 列（姓名..合同公司）+ 前 4 行
        // 打印：这份表 56 列，只有 A3 横向才看得清；页边距收窄、水平居中、每页加页码和打印日期（只设这份报表，不影响其它导出）
        sheet.PrintSetup.PaperSize = 8;   // 8 = A3
        sheet.SetMargin(MarginType.LeftMargin,   0.25);
        sheet.SetMargin(MarginType.RightMargin,  0.25);
        sheet.SetMargin(MarginType.TopMargin,    0.4);
        sheet.SetMargin(MarginType.BottomMargin, 0.5);
        sheet.HorizontallyCenter = true;
        sheet.Footer.Center = "第 &P 页 / 共 &N 页";
        sheet.Footer.Right  = "打印日期 &D";
        sheet.SetZoom(90);

        // ── 数据区：白底，不加斑马纹（业务决定这份表颜色统一成白色）；颜色只用在夜班/休息日底色和异常字色上 ──
        ICellStyle Txt(bool left, int col) => st.Get(h: left ? HorizontalAlignment.Left : HorizontalAlignment.Center, indent: (short)(left ? 1 : 0), edge: edgeCols.Contains(col));
        ICellStyle Num(int col, string color = "262626")   // 统计数字：工时列 0.0、分钟列千分位、其余常规
            => st.Get(font: color, format: hourCols.Contains(col) ? HourFormat : minuteCols.Contains(col) ? "#,##0" : null, edge: edgeCols.Contains(col));
        const string orange = "C55A11", red = "C00000";
        // 写一个统计数字格子：值为 0 且 blankIfZero 时留空（保持"0 不显示，方便一眼看出谁有问题"），但格子和边框照常创建，格线才连续
        static void Put(IRow r, int col, double v, ICellStyle style, bool blankIfZero)
        {
            var cell = r.CreateCell(col);
            cell.CellStyle = style;
            if (!(blankIfZero && v == 0)) cell.SetCellValue(v);
        }

        for (var r = 0; r < result.Rows.Count; r++)
        {
            var row = result.Rows[r];
            var xRow = sheet.CreateRow(r + 4);
            xRow.HeightInPoints = 18;

            SetCell(xRow, 0, row.RealName,             Txt(true, 0));
            SetCell(xRow, 1, row.GroupName ?? "",       Txt(true, 1));
            SetCell(xRow, 2, row.DeptName ?? "",        Txt(true, 2));
            SetCell(xRow, 3, row.EmployeeNo ?? "",      Txt(false, 3));
            SetCell(xRow, 4, row.Position ?? "",        Txt(true, 4));
            SetCell(xRow, 5, row.ContractCompany ?? "", Txt(true, 5));

            for (var i = 0; i < dayCount; i++)
            {
                // 每日格子不管有没有值都要创建，夜班淡黄底/休息浅灰底才能连成一整块（不然没打卡的格子会漏标）
                var cell = xRow.CreateCell(fixedCols + i);
                var hasHours = row.DailyHours[i] is not null;
                var isRest   = i < row.DailyIsRest.Count && row.DailyIsRest[i];
                var isZero   = row.DailyHours[i] == 0m;
                var fill = row.DailyIsNightShift[i] ? "FFF2CC"
                         : isRest && !hasHours      ? "F2F2F2"
                         :                            null;
                cell.CellStyle = st.Get(fill: fill, font: isZero ? "BFBFBF" : "262626", edge: fixedCols + i == lastDayCol);   // 当天工时 0 用浅灰字，不抢眼
                if (row.DailyHours[i] is { } h) cell.SetCellValue((double)h);
            }

            var c = tailStart;
            Put(xRow, c, (double)row.ActualWorkdays, Num(c), false); c++;
            Put(xRow, c, (double)row.LeaveDays, Num(c), true); c++;
            Put(xRow, c, row.RestDays, Num(c), true); c++;
            // 总工时（正班+加班）跟正班工时统一口径后两列数值完全相同，删掉这一列，只保留"正班工时"
            // （加班已经单独有"加班总时长"及其细分列），避免同一张表里出现两列数字永远一样的困惑
            Put(xRow, c, (double)row.RegularWorkHours, Num(c), false); c++;
            Put(xRow, c, row.LateMinutes, Num(c, row.LateMinutes > 0 ? orange : "262626"), true); c++;
            Put(xRow, c, row.EarlyLeaveCount, Num(c, row.EarlyLeaveCount > 0 ? orange : "262626"), true); c++;
            Put(xRow, c, row.LateCount, Num(c, row.LateCount > 0 ? orange : "262626"), true); c++;
            Put(xRow, c, row.EarlyLeaveMinutes, Num(c, row.EarlyLeaveMinutes > 0 ? orange : "262626"), true); c++;
            Put(xRow, c, row.MissingClockInCount, Num(c, row.MissingClockInCount > 0 ? red : "262626"), true); c++;
            Put(xRow, c, row.MissingClockOutCount, Num(c, row.MissingClockOutCount > 0 ? red : "262626"), true); c++;
            Put(xRow, c, row.AbsentDays, Num(c, row.AbsentDays > 0 ? red : "262626"), true); c++;
            Put(xRow, c, (double)row.BusinessTripHours, Num(c), true); c++;
            Put(xRow, c, row.NightShiftDays, Num(c), true); c++;
            Put(xRow, c, (double)row.NightShiftHours, Num(c), true); c++;
            Put(xRow, c, (double)row.TotalOvertimeHours, Num(c), true); c++;
            Put(xRow, c, (double)row.WeekdayOvertimeHours, Num(c), true); c++;
            Put(xRow, c, (double)row.RestDayOvertimeHours, Num(c), true); c++;
            Put(xRow, c, row.ExpectedWorkdays, Num(c), false);   // 应出勤天数（放最后一列）
        }

        // 筛选箭头：列名行是第 4 行（下标 3），范围到最后一条员工数据为止——合计行在范围外，不会被筛掉或排序打乱
        var lastDataRow = 4 + Math.Max(result.Rows.Count, 1) - 1;
        sheet.SetAutoFilter(new CellRangeAddress(3, lastDataRow, 0, totalCols - 1));

        // 合计行：每日格子和尾部统计列各自求和。用 SUBTOTAL(109,…) 而不是 SUM：HR 用筛选箭头筛出"生产部+有旷工"的人之后，
        // 合计行只统计可见的行；没筛选时就是全表合计。（"出勤天数"是天数求和，不是人数。）
        if (result.Rows.Count > 0)
        {
            var totalRowIndex = 4 + result.Rows.Count;
            var tRow = sheet.CreateRow(totalRowIndex);
            tRow.HeightInPoints = 22;
            var totalLabel = st.Get(fill: "D6DCE4", font: "1F3864", bold: true, h: HorizontalAlignment.Left, indent: 1, topMedium: true);
            SetCell(tRow, 0, "合计（随筛选结果变化）", totalLabel);
            for (var col = 1; col < fixedCols; col++) SetCell(tRow, col, "", st.Get(fill: "D6DCE4", font: "1F3864", bold: true, h: HorizontalAlignment.Left, edge: edgeCols.Contains(col), topMedium: true));
            sheet.AddMergedRegion(new CellRangeAddress(totalRowIndex, totalRowIndex, 0, fixedCols - 1));
            var firstExcelRow = 5;                       // Excel 行号 = 下标 + 1；第一条数据的下标是 4
            var lastExcelRow  = 4 + result.Rows.Count;   // 最后一条数据的下标是 3 + Rows.Count，对应 Excel 行号 4 + Rows.Count
            for (var col = fixedCols; col < totalCols; col++)
            {
                var letter = CellReference.ConvertNumToColString(col);
                var isDay  = col < tailStart;
                var fmt = hourCols.Contains(col) ? "#,##0.0;-#,##0.0;\"-\""
                        : minuteCols.Contains(col) ? "#,##0;-#,##0;\"-\""
                        : "General;-General;\"-\"";
                var cell = tRow.CreateCell(col);
                // 每日合计动辄三四千小时，窄列里会显示成"###"：字号缩到 8，放不下时自动缩小
                cell.CellStyle = st.Get(fill: "D6DCE4", font: "1F3864", size: isDay ? 8 : 10, bold: true, format: fmt, edge: edgeCols.Contains(col), topMedium: true, shrink: isDay);
                cell.SetCellFormula($"SUBTOTAL(109,{letter}{firstExcelRow}:{letter}{lastExcelRow})");
            }
            // 公式的计算结果先算好缓存进文件：Excel/WPS 打开时会自己重算，但用程序（或预览器）读取时才不会读到空值
            wb.GetCreationHelper().CreateFormulaEvaluator().EvaluateAll();
        }

        return ToBytes(wb);
    }

    // 工时列的数字格式：1 位小数、0 显示"-"、负数带负号
    private const string HourFormat = "0.0;-0.0;\"-\"";

    /// <summary>
    /// 模板月度汇总表专用的样式工厂：用任意 RGB 色（NPOI 自带的老调色板里没有这些柔和的颜色），按参数组合缓存——
    /// 同样参数只建一次样式对象。xlsx 的样式数量有上限（约 6.4 万），"普通/夜班/休息 × 是否分组边界列 × 各种字色和数字格式"
    /// 的组合总共只有几十种，靠这个缓存才不会在几千行的循环里反复新建。
    /// </summary>
    private sealed class TemplateReportStyles(XSSFWorkbook wb)
    {
        private readonly Dictionary<string, ICellStyle> _styles = [];
        private readonly Dictionary<string, IFont> _fonts = [];
        private readonly IDataFormat _fmt = wb.CreateDataFormat();

        private static XSSFColor Rgb(string hex)
        {
            var c = new XSSFColor();
            c.SetRgb(Convert.FromHexString(hex));
            return c;
        }

        private IFont Font(double size, string color, bool bold)
        {
            var key = $"{size}|{color}|{bold}";
            if (_fonts.TryGetValue(key, out var cached)) return cached;
            var f = (XSSFFont)wb.CreateFont();
            f.FontName = ReportFontName;
            f.FontHeightInPoints = size;
            f.IsBold = bold;
            f.SetColor(Rgb(color));
            return _fonts[key] = f;
        }

        /// <param name="fill">底色（RGB 十六进制），null=不填色</param>
        /// <param name="edge">true=分组的最后一列，右边线用深一点的颜色当分组分隔线</param>
        /// <param name="border">false=不画边框（标题、说明文字）</param>
        /// <param name="bottomMedium">下边线中粗深蓝（列名行下面那条线）</param>
        /// <param name="topMedium">上边线中粗深蓝（合计行上面那条线）</param>
        public ICellStyle Get(string? fill = null, string font = "262626", double size = 10, bool bold = false,
            HorizontalAlignment h = HorizontalAlignment.Center, bool wrap = false, string? format = null,
            bool edge = false, bool border = true, bool bottomMedium = false, bool topMedium = false,
            short indent = 0, bool shrink = false)
        {
            var key = $"{fill}|{font}|{size}|{bold}|{h}|{wrap}|{format}|{edge}|{border}|{bottomMedium}|{topMedium}|{indent}|{shrink}";
            if (_styles.TryGetValue(key, out var cached)) return cached;

            var s = (XSSFCellStyle)wb.CreateCellStyle();
            s.SetFont(Font(size, font, bold));
            s.Alignment = h;
            s.VerticalAlignment = VerticalAlignment.Center;
            s.WrapText = wrap;
            s.Indention = indent;
            s.ShrinkToFit = shrink;
            if (format is not null) s.DataFormat = _fmt.GetFormat(format);
            if (fill is not null)
            {
                s.SetFillForegroundColor(Rgb(fill));
                s.FillPattern = FillPattern.SolidForeground;
            }
            if (border)
            {
                var light = Rgb("D9D9D9");
                var navy  = Rgb("1F3864");
                s.BorderTop = topMedium ? BorderStyle.Medium : BorderStyle.Thin;
                s.BorderBottom = bottomMedium ? BorderStyle.Medium : BorderStyle.Thin;
                s.BorderLeft = s.BorderRight = BorderStyle.Thin;
                s.SetTopBorderColor(topMedium ? navy : light);
                s.SetBottomBorderColor(bottomMedium ? navy : light);
                s.SetLeftBorderColor(light);
                s.SetRightBorderColor(edge ? Rgb("8497B0") : light);
            }
            return _styles[key] = s;
        }
    }

    // ── 报表 3.2：排班记录（排班页导出，一行一人，每天一格显示排的什么班，方便看有没有人漏排）──
    public static byte[] ExportShiftAssignments(
        List<DateOnly> dates,
        List<(string RealName, string EmployeeNo, string GroupName, string? DeptName, List<string?> DailyShiftName, List<bool> DailyMissing, int MissingCount)> rows)
    {
        // dates 为空时 dates.First()/dates.Last() 会抛出没什么信息量的 InvalidOperationException，
        // 这里提前判断，抛出一个说得清楚原因的异常
        if (dates.Count == 0) throw new ArgumentException("dates 不能为空", nameof(dates));

        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("排班记录");

        var titleStyle    = TitleStyle(wb);
        var subTitleStyle = DataStyle(wb);
        var headerStyle   = HeaderStyleNoFill(wb);
        var dataStyle     = DataStyle(wb);
        var missingStyle  = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Red.Index);   // 漏排的格子标红，一眼看出来

        var dayCount  = dates.Count;
        var fixedCols = 4;    // 姓名/工号/考勤组/部门
        var tailCols  = 1;    // 漏排班天数
        var totalCols = fixedCols + dayCount + tailCols;

        var titleRow = sheet.CreateRow(0);
        SetCell(titleRow, 0, $"排班记录 统计日期：{dates.First():yyyy-MM-dd} 至 {dates.Last():yyyy-MM-dd}", titleStyle);
        sheet.AddMergedRegion(new CellRangeAddress(0, 0, 0, totalCols - 1));
        titleRow.HeightInPoints = 26;

        var genRow = sheet.CreateRow(1);
        SetCell(genRow, 0, $"报表生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}", subTitleStyle);
        sheet.AddMergedRegion(new CellRangeAddress(1, 1, 0, totalCols - 1));

        var headerRow = sheet.CreateRow(2);
        headerRow.HeightInPoints = 20;
        string[] fixedHeaders = ["姓名", "工号", "考勤组", "部门"];
        for (var i = 0; i < fixedHeaders.Length; i++) SetCell(headerRow, i, fixedHeaders[i], headerStyle);
        string[] weekLabel = ["日", "一", "二", "三", "四", "五", "六"];   // DayOfWeek: 0=周日...6=周六
        for (var i = 0; i < dayCount; i++)
        {
            var date = dates[i];
            SetCell(headerRow, fixedCols + i, $"{date.Month}/{date.Day} 周{weekLabel[(int)date.DayOfWeek]}", headerStyle);
        }
        SetCell(headerRow, fixedCols + dayCount, "漏排班天数", headerStyle);

        sheet.SetColumnWidth(0, 10 * 256);
        sheet.SetColumnWidth(1, 12 * 256);
        sheet.SetColumnWidth(2, 16 * 256);
        sheet.SetColumnWidth(3, 12 * 256);
        for (var i = 0; i < dayCount; i++) sheet.SetColumnWidth(fixedCols + i, 9 * 256);
        sheet.SetColumnWidth(fixedCols + dayCount, 11 * 256);
        ApplyLookAndFeel(sheet, freezeCols: fixedCols, freezeRows: 3, repeatHeaderRows: 3);

        for (var r = 0; r < rows.Count; r++)
        {
            var row  = rows[r];
            var xRow = sheet.CreateRow(r + 3);

            SetCell(xRow, 0, row.RealName,             dataStyle);
            SetCell(xRow, 1, row.EmployeeNo,            dataStyle);
            SetCell(xRow, 2, row.GroupName,             dataStyle);
            SetCell(xRow, 3, row.DeptName ?? "",        dataStyle);

            for (var i = 0; i < dayCount; i++)
            {
                if (row.DailyShiftName[i] is { } name)
                {
                    SetCell(xRow, fixedCols + i, name, dataStyle);
                }
                else if (row.DailyMissing[i])
                {
                    SetCell(xRow, fixedCols + i, "未排班", missingStyle);   // 该上班却没排班，格子直接标红，一眼看出漏在哪天
                }
                else
                {
                    xRow.CreateCell(fixedCols + i).CellStyle = dataStyle;   // 休息日/节假日/入职前，留空白格子
                }
            }

            SetCell(xRow, fixedCols + dayCount, row.MissingCount, row.MissingCount > 0 ? missingStyle : dataStyle);
        }

        return ToBytes(wb);
    }

    // ── 报表 3.5：打卡时间表（月度报表页，按部门范围导出多个人的每日打卡明细，一行一人一天）──
    public static byte[] ExportClockTimeSheet(List<AttendanceRecordDto> records, DateOnly start, DateOnly end)
    {
        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("打卡时间表");

        var titleStyle    = TitleStyle(wb);
        var subTitleStyle = DataStyle(wb);
        var headerStyle   = HeaderStyle(wb);
        var dataStyle     = DataStyle(wb);
        var bandedStyle   = BandedStyle(wb);
        // 异常状态的颜色样式，白底/斑马纹底各准备一份，避免在循环里反复新建样式对象
        var redStyle          = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Red.Index);
        var redBandedStyle    = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Red.Index, banded: true);
        var orangeStyle       = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Orange.Index);
        var orangeBandedStyle = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Orange.Index, banded: true);

        // 第 0 行：大标题（前 14 列合并）
        var titleRow = sheet.CreateRow(0);
        SetCell(titleRow, 0, $"员工打卡时间表 统计日期：{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}", titleStyle);
        sheet.AddMergedRegion(new CellRangeAddress(0, 0, 0, 13));
        titleRow.HeightInPoints = 26;

        // 第 1 行：报表生成时间
        var genRow = sheet.CreateRow(1);
        SetCell(genRow, 0, $"报表生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}", subTitleStyle);
        sheet.AddMergedRegion(new CellRangeAddress(1, 1, 0, 13));

        // 第 2 行：表头
        string[] headers = ["工号", "姓名", "部门", "日期", "星期", "上班打卡", "午间打卡", "下班打卡", "考勤状态", "实际工时(h)", "加班(h)", "请假(h)", "迟到(分)", "备注/审批"];
        var headerRow = sheet.CreateRow(2);
        headerRow.HeightInPoints = 20;
        for (var i = 0; i < headers.Length; i++)
        {
            SetCell(headerRow, i, headers[i], headerStyle);
            sheet.SetColumnWidth(i, i switch { 2 => 16 * 256, 13 => 22 * 256, _ => 11 * 256 });
        }
        ApplyLookAndFeel(sheet, freezeCols: 3, freezeRows: 3, repeatHeaderRows: 3);   // 冻结"工号/姓名/部门"这几列+表头

        // 每人每天一行；换到下一个人时切换一次斑马纹底色，把每个人的一段日期从视觉上分隔开
        string? lastEmployeeNo = null;
        var banded = false;
        for (var i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (rec.EmployeeNo != lastEmployeeNo) { banded = !banded; lastEmployeeNo = rec.EmployeeNo; }
            var baseStyle = banded ? bandedStyle : dataStyle;

            var statusStyle = rec.AttendanceStatus is AttendanceStatus.Absent or AttendanceStatus.NotPunched
                ? (banded ? redBandedStyle : redStyle)
                : rec.AttendanceStatus is AttendanceStatus.Late or AttendanceStatus.EarlyLeave
                ? (banded ? orangeBandedStyle : orangeStyle)
                : baseStyle;

            var row = sheet.CreateRow(i + 3);
            SetCell(row, 0,  rec.EmployeeNo ?? "", baseStyle);
            SetCell(row, 1,  rec.RealName ?? "",   baseStyle);
            SetCell(row, 2,  rec.DeptName ?? "",   baseStyle);
            SetCell(row, 3,  rec.WorkDateText,     baseStyle);
            SetCell(row, 4,  rec.DayOfWeekText,    baseStyle);
            SetCell(row, 5,  rec.ClockInText,      baseStyle);
            SetCell(row, 6,  rec.MidCheckTimeText, baseStyle);
            SetCell(row, 7,  rec.ClockOutText,     baseStyle);
            SetCell(row, 8,  rec.StatusText,       statusStyle);
            SetCell(row, 9,  (double)AttendanceService.FloorToHalf(rec.ActualWorkHours), baseStyle);
            SetCell(row, 10, (double)AttendanceService.FloorToHalf(rec.OvertimeHours),   baseStyle);
            SetCell(row, 11, (double)AttendanceService.FloorToHalf(rec.LeaveHours),      baseStyle);
            SetCell(row, 12, rec.LateMinutes, rec.LateMinutes > 0 ? (banded ? orangeBandedStyle : orangeStyle) : baseStyle);
            SetCell(row, 13, rec.ApprovalNote ?? "", baseStyle);
        }

        return ToBytes(wb);
    }

    // ── 报表 4：员工信息表（"员工信息"页按部门/关键字筛选后导出，一行一个人）───────
    public static byte[] ExportEmployeeList(List<User> users)
    {
        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("员工信息");

        var titleStyle  = TitleStyle(wb);
        var headerStyle = HeaderStyle(wb);
        var dataStyle   = DataStyle(wb);
        var bandedStyle = BandedStyle(wb);

        string[] headers =
        [
            "工号", "姓名", "角色", "用工性质", "状态", "部门", "考勤组", "岗位",
            "手机号", "身份证号", "合同公司", "入职日期", "停用/离职时间", "家庭住址",
            "紧急联系人", "紧急联系人电话"
        ];

        var titleRow = sheet.CreateRow(0);
        SetCell(titleRow, 0, $"员工信息表（共 {users.Count} 人）", titleStyle);
        sheet.AddMergedRegion(new CellRangeAddress(0, 0, 0, headers.Length - 1));
        titleRow.HeightInPoints = 28;

        var headerRow = sheet.CreateRow(1);
        headerRow.HeightInPoints = 20;
        for (var i = 0; i < headers.Length; i++)
        {
            SetCell(headerRow, i, headers[i], headerStyle);
            sheet.SetColumnWidth(i, 14 * 256);
        }
        sheet.SetAutoFilter(new CellRangeAddress(1, 1, 0, headers.Length - 1));
        ApplyLookAndFeel(sheet, freezeCols: 2, freezeRows: 2, repeatHeaderRows: 2);   // 冻结"工号/姓名"这两列+标题表头

        for (var r = 0; r < users.Count; r++)
        {
            var u = users[r];
            var row = sheet.CreateRow(r + 2);
            var baseStyle = r % 2 == 1 ? bandedStyle : dataStyle;
            SetCell(row, 0,  u.EmployeeNo, baseStyle);
            SetCell(row, 1,  u.RealName, baseStyle);
            SetCell(row, 2,  u.Role.ToDisplayName(), baseStyle);
            SetCell(row, 3,  u.EmploymentTypeText, baseStyle);
            SetCell(row, 4,  u.Status.ToDisplayName(), baseStyle);
            SetCell(row, 5,  u.Department?.DeptName ?? "", baseStyle);
            SetCell(row, 6,  u.AttendanceGroup?.GroupName ?? "", baseStyle);
            SetCell(row, 7,  u.Position ?? "", baseStyle);
            SetCell(row, 8,  u.Phone ?? "", baseStyle);
            SetCell(row, 9,  u.IdNumber ?? "", baseStyle);
            SetCell(row, 10, u.ContractCompany ?? "", baseStyle);
            SetCell(row, 11, u.HireDate?.ToString("yyyy-MM-dd") ?? "", baseStyle);
            SetCell(row, 12, u.DeactivatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "", baseStyle);
            SetCell(row, 13, u.HomeAddress ?? "", baseStyle);
            SetCell(row, 14, u.EmergencyContactName ?? "", baseStyle);
            SetCell(row, 15, u.EmergencyContactPhone ?? "", baseStyle);
        }

        return ToBytes(wb);
    }

    // ── 总审批记录：员工提交的申请 + 各级审批结果（一行一张申请单）────────────────────
    public static byte[] ExportApprovalRecords(List<ApprovalRequestDto> items, DateOnly start, DateOnly end)
    {
        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("总审批记录");

        var titleStyle  = TitleStyle(wb);
        var headerStyle = HeaderStyle(wb);
        var dataStyle   = DataStyle(wb);
        var bandedStyle = BandedStyle(wb);
        // 申请内容/理由/审批流程可能很长且有换行，这几列用自动换行 + 左对齐，不用居中
        ICellStyle Wrap(ICellStyle basis)
        {
            var s = wb.CreateCellStyle();
            s.CloneStyleFrom(basis);
            s.WrapText = true;
            s.Alignment = HorizontalAlignment.Left;
            s.VerticalAlignment = VerticalAlignment.Top;
            return s;
        }
        var wrapData   = Wrap(dataStyle);
        var wrapBanded = Wrap(bandedStyle);

        string[] headers =
        [
            "申请单号", "类型", "申请人工号", "申请人", "部门", "申请内容", "时长", "申请理由",
            "提交时间", "当前状态", "审批流程 / 意见", "最后处理时间"
        ];
        int[] widths = [20, 8, 12, 10, 16, 42, 12, 30, 17, 10, 46, 17];

        var titleRow = sheet.CreateRow(0);
        SetCell(titleRow, 0, $"总审批记录（提交时间 {start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}，共 {items.Count} 条）", titleStyle);
        sheet.AddMergedRegion(new CellRangeAddress(0, 0, 0, headers.Length - 1));
        titleRow.HeightInPoints = 28;

        var headerRow = sheet.CreateRow(1);
        headerRow.HeightInPoints = 20;
        for (var i = 0; i < headers.Length; i++)
        {
            SetCell(headerRow, i, headers[i], headerStyle);
            sheet.SetColumnWidth(i, widths[i] * 256);
        }
        sheet.SetAutoFilter(new CellRangeAddress(1, 1, 0, headers.Length - 1));
        ApplyLookAndFeel(sheet, freezeCols: 4, freezeRows: 2, repeatHeaderRows: 2);   // 冻结单号/类型/工号/姓名这几列 + 标题表头

        for (var r = 0; r < items.Count; r++)
        {
            var a = items[r];
            var row = sheet.CreateRow(r + 2);
            var baseStyle = r % 2 == 1 ? bandedStyle : dataStyle;
            var textStyle = r % 2 == 1 ? wrapBanded : wrapData;
            var lastHandled = a.Steps.Where(s => s.HandledAt.HasValue).Select(s => s.HandledAt!.Value).DefaultIfEmpty().Max();
            SetCell(row, 0,  a.RequestNo, baseStyle);
            SetCell(row, 1,  a.ApprovalTypeText, baseStyle);
            SetCell(row, 2,  a.ApplicantEmployeeNo, baseStyle);
            SetCell(row, 3,  a.ApplicantName, baseStyle);
            SetCell(row, 4,  a.DeptName ?? "", baseStyle);
            SetCell(row, 5,  a.ContentText, textStyle);
            SetCell(row, 6,  a.DurationText, baseStyle);
            SetCell(row, 7,  a.Reason ?? "", textStyle);
            SetCell(row, 8,  a.SubmittedAtText, baseStyle);
            SetCell(row, 9,  a.ApprovalStatusText, baseStyle);
            SetCell(row, 10, a.StepsText, textStyle);
            SetCell(row, 11, lastHandled == default ? "" : lastHandled.ToString("yyyy-MM-dd HH:mm"), baseStyle);
            var lines = Math.Max(1, a.StepsText.Split('\n').Length);
            if (lines > 1) row.HeightInPoints = Math.Min(15 * lines, 120);   // 多级审批时把这行撑高，方便看全
        }

        return ToBytes(wb);
    }

    // ── 下面是“样式工厂”：各做一种单元格外观（字体/颜色/边框/对齐）─────────────

    // 全部报表统一用这个字体——默认字体是英文的 Arial，中文在 Excel 里显示会发虚，换成微软雅黑更清楚
    private const string ReportFontName = "微软雅黑";

    private static ICellStyle TitleStyle(IWorkbook wb)      // 大标题：16 号、加粗、居中
    {
        var s = wb.CreateCellStyle();
        var f = wb.CreateFont();
        f.FontName = ReportFontName;
        f.FontHeightInPoints = 16;
        f.IsBold = true;
        s.SetFont(f);
        s.Alignment = HorizontalAlignment.Center;
        s.VerticalAlignment = VerticalAlignment.Center;
        return s;
    }

    private static ICellStyle HeaderStyle(IWorkbook wb)     // 表头：加粗、灰底、居中、带边框
    {
        var s = wb.CreateCellStyle();
        var f = wb.CreateFont();
        f.FontName = ReportFontName;
        f.IsBold = true;
        s.SetFont(f);
        s.FillForegroundColor = NPOI.HSSF.Util.HSSFColor.Grey25Percent.Index;
        s.FillPattern = FillPattern.SolidForeground;
        s.Alignment   = HorizontalAlignment.Center;
        s.VerticalAlignment = VerticalAlignment.Center;
        ApplyBorder(s);
        return s;
    }

    private static ICellStyle DataStyle(IWorkbook wb)       // 普通数据：居中、带边框
    {
        var s = wb.CreateCellStyle();
        var f = wb.CreateFont();
        f.FontName = ReportFontName;
        s.SetFont(f);
        s.Alignment = HorizontalAlignment.Center;
        s.VerticalAlignment = VerticalAlignment.Center;
        ApplyBorder(s);
        return s;
    }

    // 在普通样式基础上改字体颜色；banded=true 则打底用斑马纹底色，方便异常字体色和分组底色同时叠加显示
    private static ICellStyle ColorStyle(IWorkbook wb, short colorIndex, bool banded = false)
    {
        var s = banded ? BandedStyle(wb) : DataStyle(wb);
        var f = wb.CreateFont();
        f.FontName = ReportFontName;
        f.Color = colorIndex;
        s.SetFont(f);
        return s;
    }

    // 隔行浅色底（斑马纹）：人多、天数多的时候，一行数据横向很长，加了这个更容易顺着一行看到底不串行。
    // 注意：NPOI 自带的灰色只有 Grey25/40/50/80Percent 这几档，25% 已经被表头用了（太深，不适合做斑马纹底色），
    // 所以斑马纹改用浅蓝（PaleBlue）——足够浅、不抢眼，又能跟表头的深灰区分开。
    private static ICellStyle BandedStyle(IWorkbook wb)
    {
        var s = DataStyle(wb);
        s.FillForegroundColor = NPOI.HSSF.Util.HSSFColor.PaleBlue.Index;
        s.FillPattern = FillPattern.SolidForeground;
        return s;
    }

    // 模板月度汇总表专用：表头去掉灰底、改成白底（这份报表颜色统一改成白色）。
    private static ICellStyle HeaderStyleNoFill(IWorkbook wb)
    {
        var s = wb.CreateCellStyle();
        var f = wb.CreateFont();
        f.FontName = ReportFontName;
        f.IsBold = true;
        s.SetFont(f);
        s.Alignment   = HorizontalAlignment.Center;
        s.VerticalAlignment = VerticalAlignment.Center;
        ApplyBorder(s);
        return s;
    }

    // 工时类的小数格子：固定显示 2 位小数（比如 9.5 显示成 9.50），一列数字看着整整齐齐，不会有的 1 位有的 2 位
    private static ICellStyle HourStyle(IWorkbook wb)       => WithTwoDecimals(DataStyle(wb), wb);
    private static ICellStyle BandedHourStyle(IWorkbook wb) => WithTwoDecimals(BandedStyle(wb), wb);

    private static ICellStyle WithTwoDecimals(ICellStyle s, IWorkbook wb)
    {
        s.DataFormat = wb.CreateDataFormat().GetFormat("0.00");
        return s;
    }

    private static void ApplyBorder(ICellStyle s)          // 给单元格四边加细边框
    {
        s.BorderBottom = s.BorderTop = s.BorderLeft = s.BorderRight = BorderStyle.Thin;
    }

    // 统一的打印/查看外观：关掉默认灰色网格线（改靠边框区分格子，看着更干净）、
    // 横向打印并把宽度压缩到一页（这几份报表列数都不少，横向打印才不会被切成好几页）、
    // 冻结指定的行数/列数（往下/往右滚动时，表头和姓名这些"认人"的列始终留在屏幕上）、
    // 打印多页时每页顶部都重复表头（不然翻到后面几页就不知道每一列是什么了）。
    private static void ApplyLookAndFeel(ISheet sheet, int freezeCols, int freezeRows, int repeatHeaderRows)
    {
        sheet.DisplayGridlines = false;
        sheet.PrintSetup.Landscape = true;
        sheet.FitToPage = true;
        sheet.PrintSetup.FitWidth  = 1;
        sheet.PrintSetup.FitHeight = 0;   // 0=高度不限制页数，只压缩宽度到一页
        sheet.CreateFreezePane(freezeCols, freezeRows);
        sheet.RepeatingRows = new CellRangeAddress(0, repeatHeaderRows - 1, 0, 0);
    }

    // ── SetCell：往某个单元格写值并套样式 ──────────────────────────────────────
    // 这里写了 3 个同名的 SetCell 方法，唯一区别是 value 参数的类型不同（文字/整数/小数）。
    // 这种"同一个名字、参数类型不同"的写法叫"方法重载"：调用的时候，C# 会自动根据你传的值
    // 是文字还是数字，去匹配对应的那一个，不需要写 SetCellText / SetCellInt 这种不同名字。
    // 之前这里加过一道"内容以 = + - @ 开头就前置一个单引号"的公式注入防护（OWASP 的 CSV/Excel
    // Injection），后来实测发现对这里不适用、而且有副作用，撤掉了：POI 的 SetCellValue(string)
    // 存的是纯字符串类型的单元格（CellType.String），不是公式类型（Formula，对应 xlsx 里的 <f> 元素），
    // Excel/WPS 打开原生 xlsx 时不会把字符串单元格的内容重新解析成公式——这跟"把一份 CSV 文本文件
    // 导入 Excel"是完全不同的两条路径，公式注入风险主要出在 CSV 导入这一类场景，这个项目目前没有
    // CSV 导出功能。而且 Excel 的"前导单引号=强制文本"是它自己编辑器输入时的行为（单引号本身不会
    // 存进单元格内容，只是给这个单元格的样式打一个"强制文本"标记），不是文件格式层面的规则——
    // 这里手动拼进字符串值的单引号会被原样存成看得见的字符，打开后会显示成"'张三"这种带撇号的
    // 姓名，反而是个新问题。以后如果这个项目真的要做 CSV 导出，需要在那条路径上单独处理这个问题。
    private static void SetCell(IRow row, int col, string value, ICellStyle style)
    {
        var c = row.CreateCell(col); c.SetCellValue(value); c.CellStyle = style;
    }
    private static void SetCell(IRow row, int col, int value, ICellStyle style)
    {
        var c = row.CreateCell(col); c.SetCellValue(value); c.CellStyle = style;
    }
    private static void SetCell(IRow row, int col, double value, ICellStyle style)
    {
        var c = row.CreateCell(col); c.SetCellValue(value); c.CellStyle = style;
    }

    // 值为 0 时不写（留空白格子），非 0 才写——模板月度汇总表里，迟到/早退/缺卡/旷工这类"异常次数"
    // 统一按这个规则显示，0 次留空更方便肉眼一眼看出哪些人有问题，不用满屏都是 0。
    private static void SetCellIfNonZero(IRow row, int col, int value, ICellStyle style)
    {
        if (value != 0) SetCell(row, col, value, style);
    }
    private static void SetCellIfNonZero(IRow row, int col, double value, ICellStyle style)
    {
        if (value != 0) SetCell(row, col, value, style);
    }

    // 把内存里拼好的 Excel 写成字节数组返回
    private static byte[] ToBytes(XSSFWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.Write(ms, leaveOpen: true);
        return ms.ToArray();
    }
}
