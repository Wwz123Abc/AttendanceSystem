using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using NPOI.XSSF.UserModel;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>月度报表"导出员工原始打卡记录"：一次打卡一行，列和内容要对。</summary>
public class RawPunchExportTests
{
    [Fact]
    public void 原始打卡导出_一次打卡一行_类型来源定位都写对()
    {
        var d = new DateTime(2026, 10, 19);
        List<RawPunchExportRow> rows =
        [
            new("A001", "张三", "装配组", "交付部考勤组", d.AddHours(8).AddMinutes(25).AddSeconds(10), 1, "MobileFace", "深圳某地", 22.5, 114.1, true, true, d.AddHours(8.5)),
            new("A001", "张三", "装配组", "交付部考勤组", d.AddHours(12).AddMinutes(20), 3, "ZKDevice:SN001", null, null, null, null, true, d.AddHours(12.4)),
            new("A001", "张三", "装配组", "交付部考勤组", d.AddHours(21).AddMinutes(5), 2, "管理员补卡:李四", null, null, null, null, false, d.AddHours(22)),
        ];
        var bytes = ExcelExportHelper.ExportRawPunches(rows, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 19));
        using var ms = new MemoryStream(bytes);
        var sheet = new XSSFWorkbook(ms).GetSheet("原始打卡记录");

        Assert.Equal(4, sheet.PhysicalNumberOfRows);   // 表头 + 3 次打卡
        Assert.Equal("工号", sheet.GetRow(0).GetCell(0).StringCellValue);
        var r1 = sheet.GetRow(1);
        Assert.Equal("A001", r1.GetCell(0).StringCellValue);
        Assert.Equal("2026-10-19", r1.GetCell(4).StringCellValue);
        Assert.Equal("周一", r1.GetCell(5).StringCellValue);
        Assert.Equal("08:25:10", r1.GetCell(6).StringCellValue);
        Assert.Equal("上班", r1.GetCell(7).StringCellValue);
        Assert.Equal("手机人脸", r1.GetCell(8).StringCellValue);
        Assert.Equal("深圳某地", r1.GetCell(10).StringCellValue);
        Assert.Equal("是", r1.GetCell(13).StringCellValue);
        var r2 = sheet.GetRow(2);
        Assert.Equal("午间", r2.GetCell(7).StringCellValue);
        Assert.Equal("考勤机", r2.GetCell(8).StringCellValue);
        Assert.Equal("", r2.GetCell(13).StringCellValue);   // 没有定位信息
        var r3 = sheet.GetRow(3);
        Assert.Equal("下班", r3.GetCell(7).StringCellValue);
        Assert.Equal("管理员补卡", r3.GetCell(8).StringCellValue);
        Assert.Equal("无效", r3.GetCell(14).StringCellValue);
    }
}
