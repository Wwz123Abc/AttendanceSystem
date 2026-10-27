using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>离职日期（2026-10-27）：停用/拉黑默认填当天（已填过的不覆盖），重新启用清空，编辑保存能改，月度报表 Excel 最后一列带出。</summary>
public class ResignDateTests : SqliteTestBase
{
    // 管理员已经在第一次 NewUserService 里建好了，这里只按同样方式拼出服务，不再重复建管理员（工号会冲突）
    private UserService NewUserServiceFor(AttendanceDbContext db)
    {
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var zk = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        return new UserService(db, zk, AppOptions, new DeptScopeService(db), NullLogger<UserService>.Instance);
    }

    private async Task<int> AddEmployeeAsync(string no, DateOnly? resign = null)
    {
        using var db = CreateContext();
        var u = U(no, "员工" + no);
        u.ResignDate = resign;
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    [Fact]
    public async Task 停用时离职日期默认填当天_重新启用后清空()
    {
        var id = await AddEmployeeAsync("RD1");
        int adminId;
        using (var db = CreateContext())
        {
            var (svc, aid) = NewUserService(db); adminId = aid;
            Assert.True(await svc.DeactivateUserAsync(id, adminId));
        }
        using (var check = CreateContext())
            Assert.Equal(DateOnly.FromDateTime(DateTime.Now), (await check.Users.FindAsync(id))!.ResignDate);

        using (var db = CreateContext())
        {
            var svc = NewUserServiceFor(db);
            Assert.True(await svc.ActivateUserAsync(id, adminId));
        }
        using var check2 = CreateContext();
        Assert.Null((await check2.Users.FindAsync(id))!.ResignDate);
    }

    [Fact]
    public async Task 停用时已经填过离职日期_不被覆盖成当天()
    {
        var planned = new DateOnly(2026, 10, 20);
        var id = await AddEmployeeAsync("RD2", planned);
        using (var db = CreateContext())
        {
            var (svc, adminId) = NewUserService(db);
            Assert.True(await svc.DeactivateUserAsync(id, adminId));
        }
        using var check = CreateContext();
        Assert.Equal(planned, (await check.Users.FindAsync(id))!.ResignDate);
    }

    [Fact]
    public async Task 编辑员工可以保存和清空离职日期()
    {
        var id = await AddEmployeeAsync("RD3");
        int adminId;
        using (var db = CreateContext())
        {
            var (svc, aid) = NewUserService(db); adminId = aid;
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == id);
            u.ResignDate = new DateOnly(2026, 11, 1);
            Assert.True(await svc.UpdateUserAsync(u, adminId));
        }
        using (var check = CreateContext())
            Assert.Equal(new DateOnly(2026, 11, 1), (await check.Users.FindAsync(id))!.ResignDate);

        using (var db = CreateContext())
        {
            var svc = NewUserServiceFor(db);
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == id);
            u.ResignDate = null;
            Assert.True(await svc.UpdateUserAsync(u, adminId));
        }
        using var check2 = CreateContext();
        Assert.Null((await check2.Users.FindAsync(id))!.ResignDate);
    }

    [Fact]
    public void 月度报表Excel_离职日期在最后一列_前面的列位置不变()
    {
        var dates = new List<DateOnly> { new(2026, 9, 1), new(2026, 9, 2) };
        var row = new TemplateReportRowDto
        {
            RealName = "张三", EmployeeNo = "E001", ContractCompany = "聚星", ResignDate = new DateOnly(2026, 9, 2),
            DailyHours = [8m, null], DailyIsNightShift = [false, false], DailyIsRest = [false, false]
        };
        var bytes = ExcelExportHelper.ExportTemplateReport(new TemplateReportResultDto
        {
            StartDate = dates[0], EndDate = dates[^1], Dates = dates, Rows = [row]
        });
        using var wb = new NPOI.XSSF.UserModel.XSSFWorkbook(new MemoryStream(bytes));
        var sheet = wb.GetSheetAt(0);
        var header = sheet.GetRow(3);
        var data = sheet.GetRow(4);
        var last = header.LastCellNum - 1;
        Assert.Equal("离职日期", header.GetCell(last).StringCellValue);
        Assert.Equal("2026-09-02", data.GetCell(last).StringCellValue);
        Assert.Equal("合同公司", header.GetCell(5).StringCellValue);   // 固定列的位置没变
        Assert.Equal("聚星", data.GetCell(5).StringCellValue);
    }
}
