using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.SS.Util;
using AttendanceSystem.Models.DTOs;
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
        var titleStyle  = TitleStyle(wb);   // 大标题
        var headerStyle = HeaderStyle(wb);  // 表头（灰底加粗）
        var dataStyle   = DataStyle(wb);    // 普通数据
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

        // 从第 2 行起：每个员工一行数据（异常的数字用红/橙色突出）
        for (var r = 0; r < summaries.Count; r++)
        {
            var dto = summaries[r];
            var row = sheet.CreateRow(r + 2);
            SetCell(row, 0,  dto.EmployeeNo,                              dataStyle);
            SetCell(row, 1,  dto.RealName,                                dataStyle);
            SetCell(row, 2,  dto.DeptName ?? "",                          dataStyle);
            SetCell(row, 3,  dto.Position ?? "",                          dataStyle);
            SetCell(row, 4,  dto.ExpectedWorkdays,                        dataStyle);
            SetCell(row, 5,  dto.ActualWorkdays,                          dataStyle);
            SetCell(row, 6,  dto.NightShiftDays,                          dataStyle);   // 夜班天数
            SetCell(row, 7,  dto.LateCount,        dto.LateCount > 0        ? orangeStyle : dataStyle);  // 有迟到→橙
            SetCell(row, 8,  dto.EarlyLeaveCount,  dto.EarlyLeaveCount > 0  ? orangeStyle : dataStyle);  // 有早退→橙
            SetCell(row, 9,  dto.AbsentDays,       dto.AbsentDays > 0       ? redStyle    : dataStyle);  // 有旷工→红
            SetCell(row, 10, dto.NotPunchedCount,  dto.NotPunchedCount > 0  ? redStyle    : dataStyle);  // 有缺卡→红
            SetCell(row, 11, (double)dto.LeaveDays,                       dataStyle);
            SetCell(row, 12, (double)dto.TotalOvertimeHours,              dataStyle);
            SetCell(row, 13, (double)dto.TotalWorkHours,                  dataStyle);
            SetCell(row, 14, dto.ApprovedCount,                           dataStyle);
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

        // 每天一行；按考勤状态给“状态”单元格上色
        for (var i = 0; i < summary.DailyRecords.Count; i++)
        {
            var rec = summary.DailyRecords[i];
            var row = sheet.CreateRow(i + 2);

            // 旷工/缺卡→红；迟到/早退→橙；节假日→灰（这个"灰"分支目前实际上不会触发，
            // 因为 rec.IsHoliday 这个值在整个系统里从来没被真正设置过 true，一直是 false；
            // 保留这段判断是为了以后万一把 IsHoliday 接上了，颜色逻辑不用再改）；正常→默认
            var statusStyle = rec.AttendanceStatus is AttendanceStatus.Absent or AttendanceStatus.NotPunched
                ? redStyle
                : rec.AttendanceStatus is AttendanceStatus.Late or AttendanceStatus.EarlyLeave
                ? orangeStyle
                : rec.IsHoliday
                ? grayStyle
                : dataStyle;

            SetCell(row, 0, rec.WorkDateText,                dataStyle);
            SetCell(row, 1, rec.DayOfWeekText,               dataStyle);
            SetCell(row, 2, rec.ClockInText,                 dataStyle);
            SetCell(row, 3, rec.ClockOutText,                dataStyle);
            SetCell(row, 4, rec.StatusText,                  statusStyle);
            SetCell(row, 5, (double)rec.ActualWorkHours,     dataStyle);
            SetCell(row, 6, (double)rec.OvertimeHours,       dataStyle);
            SetCell(row, 7, rec.LateMinutes,
                rec.LateMinutes > 0 ? orangeStyle : dataStyle);
            SetCell(row, 8, rec.ApprovalNote ?? "",          dataStyle);
        }

        return ToBytes(wb);
    }

    // ── 下面是“样式工厂”：各做一种单元格外观（字体/颜色/边框/对齐）─────────────

    private static ICellStyle TitleStyle(IWorkbook wb)      // 大标题：16 号、加粗、居中
    {
        var s = wb.CreateCellStyle();
        var f = wb.CreateFont();
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
        f.IsBold = true;
        s.SetFont(f);
        s.FillForegroundColor = NPOI.HSSF.Util.HSSFColor.Grey25Percent.Index;
        s.FillPattern = FillPattern.SolidForeground;
        s.Alignment   = HorizontalAlignment.Center;
        ApplyBorder(s);
        return s;
    }

    private static ICellStyle DataStyle(IWorkbook wb)       // 普通数据：居中、带边框
    {
        var s = wb.CreateCellStyle();
        s.Alignment = HorizontalAlignment.Center;
        ApplyBorder(s);
        return s;
    }

    private static ICellStyle ColorStyle(IWorkbook wb, short colorIndex)  // 在普通样式基础上改字体颜色
    {
        var s = DataStyle(wb);
        var f = wb.CreateFont();
        f.Color = colorIndex;
        s.SetFont(f);
        return s;
    }

    private static void ApplyBorder(ICellStyle s)          // 给单元格四边加细边框
    {
        s.BorderBottom = s.BorderTop = s.BorderLeft = s.BorderRight = BorderStyle.Thin;
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

    // 把内存里拼好的 Excel 写成字节数组返回
    private static byte[] ToBytes(XSSFWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.Write(ms, leaveOpen: true);
        return ms.ToArray();
    }
}
