using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.SS.Util;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

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
        var wb     = new XSSFWorkbook();                              // 新建一个 Excel 文件
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
            SetCell(row, 5,  dto.ActualWorkdays,                          baseStyle);
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
            SetCell(totalRow, 5,  summaries.Sum(s => s.ActualWorkdays),                      headerStyle);
            SetCell(totalRow, 6,  summaries.Sum(s => s.NightShiftDays),                      headerStyle);
            SetCell(totalRow, 7,  summaries.Sum(s => s.LateCount),                           headerStyle);
            SetCell(totalRow, 8,  summaries.Sum(s => s.EarlyLeaveCount),                     headerStyle);
            SetCell(totalRow, 9,  summaries.Sum(s => s.AbsentDays),                          headerStyle);
            SetCell(totalRow, 10, summaries.Sum(s => s.NotPunchedCount),                     headerStyle);
            SetCell(totalRow, 12, (double)summaries.Sum(s => s.TotalOvertimeHours),          headerStyle);
            SetCell(totalRow, 13, (double)summaries.Sum(s => s.TotalWorkHours),              headerStyle);
        }

        return ToBytes(wb);   // 把 Excel 转成字节数组返回（供下载）
    }

    // ── 报表 2：个人每日考勤明细（一行一天）─────────────────────────────────────
    public static byte[] ExportDailyStatusReport(MonthlySummaryDto summary)
    {
        var wb    = new XSSFWorkbook();
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
            SetCell(row, 5, HalfFloor(rec.ActualWorkHours),  baseStyle);
            SetCell(row, 6, HalfFloor(rec.OvertimeHours),    baseStyle);
            SetCell(row, 7, rec.LateMinutes,
                rec.LateMinutes > 0 ? orangeStyle : baseStyle);
            SetCell(row, 8, rec.ApprovalNote ?? "",          baseStyle);
        }

        return ToBytes(wb);
    }

    // ── 报表 3：模板月度汇总表（对照公司要求的外部模板文件列结构，一行一个人，含每日打卡格子）──
    public static byte[] ExportTemplateReport(TemplateReportResultDto result)
    {
        var wb    = new XSSFWorkbook();
        var sheet = wb.CreateSheet("月度汇总");

        // 这份报表颜色统一改成白色（不再有隔行斑马纹/周末浅黄底）；
        // 只在"考勤结果"那组每日格子里，识别出当天是夜班的话单独标黄，一眼看出谁那天上的夜班。
        var titleStyle   = TitleStyle(wb);
        var subTitleStyle = DataStyle(wb);
        var headerStyle  = HeaderStyleNoFill(wb);
        var dataStyle    = DataStyle(wb);
        var nightShiftStyle = NightShiftStyle(wb);   // 夜班当天的格子：黄底
        var redStyle     = ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Red.Index);

        var dayCount  = result.Dates.Count;
        var fixedCols = 6;                       // 姓名/考勤组/部门/工号/职位/合同公司
        var tailCols  = 17;                       // 出勤天数..节假日加班（含夜班次数）
        var totalCols = fixedCols + dayCount + tailCols;

        // 第 0 行：大标题（统计日期区间）
        var titleRow = sheet.CreateRow(0);
        SetCell(titleRow, 0, $"月度汇总 统计日期：{result.StartDate:yyyy-MM-dd} 至 {result.EndDate:yyyy-MM-dd}", titleStyle);
        sheet.AddMergedRegion(new CellRangeAddress(0, 0, 0, totalCols - 1));
        titleRow.HeightInPoints = 26;

        // 第 1 行：报表生成时间
        var genRow = sheet.CreateRow(1);
        SetCell(genRow, 0, $"报表生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}", subTitleStyle);
        sheet.AddMergedRegion(new CellRangeAddress(1, 1, 0, totalCols - 1));

        // 第 2 行：主表头
        var headerRow = sheet.CreateRow(2);
        headerRow.HeightInPoints = 20;
        string[] fixedHeaders = ["姓名", "考勤组", "部门", "工号", "职位", "合同公司"];
        for (var i = 0; i < fixedHeaders.Length; i++) SetCell(headerRow, i, fixedHeaders[i], headerStyle);
        SetCell(headerRow, fixedCols, "考勤结果", headerStyle);
        if (dayCount > 1) sheet.AddMergedRegion(new CellRangeAddress(2, 2, fixedCols, fixedCols + dayCount - 1));
        string[] tailHeaders =
        [
            "出勤天数", "休息天数", "总工时", "迟到时长", "早退次数", "迟到次数", "早退时长",
            "上班缺卡次数", "下班缺卡次数", "旷工天数", "出差时长", "外出时长", "夜班次数",
            "加班总时长", "工作日加班", "休息日加班", "节假日加班"
        ];
        for (var i = 0; i < tailHeaders.Length; i++) SetCell(headerRow, fixedCols + dayCount + i, tailHeaders[i], headerStyle);

        // 第 3 行：每天的日期表头（周六/周日用"六"/"日"代替日期数字，和参考模板一致）
        var dayHeaderRow = sheet.CreateRow(3);
        for (var i = 0; i < dayCount; i++)
        {
            var date  = result.Dates[i];
            var label = date.DayOfWeek switch { DayOfWeek.Saturday => "六", DayOfWeek.Sunday => "日", _ => date.Day.ToString() };
            SetCell(dayHeaderRow, fixedCols + i, label, headerStyle);
        }

        // 列宽：姓名/部门等给宽一点，每日格子给窄一点
        sheet.SetColumnWidth(0, 10 * 256);
        sheet.SetColumnWidth(1, 20 * 256);
        sheet.SetColumnWidth(2, 18 * 256);
        for (var i = 3; i < fixedCols; i++) sheet.SetColumnWidth(i, 12 * 256);
        for (var i = 0; i < dayCount; i++) sheet.SetColumnWidth(fixedCols + i, 5 * 256);
        for (var i = 0; i < tailHeaders.Length; i++) sheet.SetColumnWidth(fixedCols + dayCount + i, 11 * 256);
        ApplyLookAndFeel(sheet, freezeCols: fixedCols, freezeRows: 4, repeatHeaderRows: 4);   // 冻结前 6 列（姓名..合同公司）+ 前 4 行（标题/生成时间/表头/日期表头）

        // 从第 4 行起：每个员工一行；固定列/尾部统计列统一白底，每日格子里当天是夜班的才标黄
        for (var r = 0; r < result.Rows.Count; r++)
        {
            var row = result.Rows[r];
            var xRow = sheet.CreateRow(r + 4);
            var baseStyle = dataStyle;

            SetCell(xRow, 0, row.RealName,             baseStyle);
            SetCell(xRow, 1, row.GroupName ?? "",       baseStyle);
            SetCell(xRow, 2, row.DeptName ?? "",        baseStyle);
            SetCell(xRow, 3, row.EmployeeNo ?? "",      baseStyle);
            SetCell(xRow, 4, row.Position ?? "",        baseStyle);
            SetCell(xRow, 5, row.ContractCompany ?? "", baseStyle);

            for (var i = 0; i < dayCount; i++)
            {
                // 每日格子不管有没有值都要创建，夜班黄底才能连成一整块（不然没打卡的夜班格子会漏标）
                var cell = xRow.CreateCell(fixedCols + i);
                cell.CellStyle = row.DailyIsNightShift[i] ? nightShiftStyle : baseStyle;
                if (row.DailyHours[i] is { } h) cell.SetCellValue(h);
            }

            var c = fixedCols + dayCount;
            SetCell(xRow, c++, row.ActualWorkdays, baseStyle);
            SetCellIfNonZero(xRow, c++, row.RestDays, baseStyle);
            SetCell(xRow, c++, (double)row.TotalWorkHours, baseStyle);
            SetCellIfNonZero(xRow, c++, row.LateMinutes, orangeOrRed(row.LateMinutes));
            SetCellIfNonZero(xRow, c++, row.EarlyLeaveCount, orangeOrRed(row.EarlyLeaveCount));
            SetCellIfNonZero(xRow, c++, row.LateCount, orangeOrRed(row.LateCount));
            SetCellIfNonZero(xRow, c++, row.EarlyLeaveMinutes, orangeOrRed(row.EarlyLeaveMinutes));
            SetCellIfNonZero(xRow, c++, row.MissingClockInCount, redIfPositive(row.MissingClockInCount));
            SetCellIfNonZero(xRow, c++, row.MissingClockOutCount, redIfPositive(row.MissingClockOutCount));
            SetCellIfNonZero(xRow, c++, row.AbsentDays, redIfPositive(row.AbsentDays));
            if (row.BusinessTripHours > 0) SetCell(xRow, c, (double)row.BusinessTripHours, baseStyle);
            c++;
            c++;   // 外出时长：系统没有这个概念，恒不写值（留空）
            SetCellIfNonZero(xRow, c++, row.NightShiftDays, baseStyle);
            SetCellIfNonZero(xRow, c++, (double)row.TotalOvertimeHours, baseStyle);
            SetCellIfNonZero(xRow, c++, (double)row.WeekdayOvertimeHours, baseStyle);
            SetCellIfNonZero(xRow, c++, (double)row.RestDayOvertimeHours, baseStyle);
            SetCellIfNonZero(xRow, c,   (double)row.HolidayOvertimeHours, baseStyle);
        }

        return ToBytes(wb);

        ICellStyle orangeOrRed(int v) => v > 0 ? ColorStyle(wb, NPOI.HSSF.Util.HSSFColor.Orange.Index) : dataStyle;
        ICellStyle redIfPositive(int v) => v > 0 ? redStyle : dataStyle;
    }

    // ── 报表 4：员工信息表（"员工信息"页按部门/关键字筛选后导出，一行一个人）───────
    public static byte[] ExportEmployeeList(List<User> users)
    {
        var wb    = new XSSFWorkbook();
        var sheet = wb.CreateSheet("员工信息");

        var titleStyle  = TitleStyle(wb);
        var headerStyle = HeaderStyle(wb);
        var dataStyle   = DataStyle(wb);
        var bandedStyle = BandedStyle(wb);

        string[] headers =
        [
            "工号", "姓名", "角色", "用工性质", "状态", "部门", "考勤组", "岗位",
            "手机号", "身份证号", "合同公司", "入职日期", "家庭住址",
            "紧急联系人", "紧急联系人电话", "钉钉绑定"
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
            SetCell(row, 12, u.HomeAddress ?? "", baseStyle);
            SetCell(row, 13, u.EmergencyContactName ?? "", baseStyle);
            SetCell(row, 14, u.EmergencyContactPhone ?? "", baseStyle);
            SetCell(row, 15, string.IsNullOrEmpty(u.DingTalkUserId) ? "未绑定" : "已绑定", baseStyle);
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

    private static ICellStyle ColorStyle(IWorkbook wb, short colorIndex)  // 在普通样式基础上改字体颜色
    {
        var s = DataStyle(wb);
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

    // 模板月度汇总表专用：夜班当天的每日格子标黄，一眼看出这个人这天上的是夜班
    private static ICellStyle NightShiftStyle(IWorkbook wb)
    {
        var s = DataStyle(wb);
        s.FillForegroundColor = NPOI.HSSF.Util.HSSFColor.Yellow.Index;
        s.FillPattern = FillPattern.SolidForeground;
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

    // 把工时数规范成"半小时"为最小单位（不足半小时舍去）：1.2→1.0，1.7→1.5。
    // 报表里的工时数字统一走这一道，只会出现整数或 x.5（和服务层 AttendanceService.FloorToHalf 同一口径）。
    private static double HalfFloor(decimal hours) => (double)(Math.Floor(hours * 2) / 2);

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
