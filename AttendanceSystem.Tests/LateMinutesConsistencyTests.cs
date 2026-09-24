using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 迟到/早退"分钟"与"次数"同一口径（2026-09-24 用线上真实数据核查发现）：
/// 月度汇总表里迟到分钟合计 141,595，其中 47% 来自状态已被改成"未打卡"的记录（当天只有一次很晚的打卡，
/// 先被当成上班卡算出几百分钟迟到，后台发现没下班卡又改成"未打卡"，分钟数没清），出现
/// "迟到 1618 分钟、迟到 1 次"（梁育雄）这种分钟和次数对不上的行。
/// </summary>
public class LateMinutesConsistencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());

    public LateMinutesConsistencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    private static AttendanceRecord Rec(AttendanceStatus st, int late, int early) =>
        new() { AttendanceStatus = st, LateMinutes = late, EarlyLeaveMinutes = early };

    [Theory]
    [InlineData(AttendanceStatus.Late, 267, 267)]        // 真正的迟到：算数
    [InlineData(AttendanceStatus.NotPunched, 811, 0)]    // 单次很晚的打卡被当上班卡、后来改成未打卡：不算迟到
    [InlineData(AttendanceStatus.OnLeave, 212, 0)]       // 半天假当天残留的分钟：不算（次数也不算）
    [InlineData(AttendanceStatus.Normal, 3, 0)]
    [InlineData(AttendanceStatus.Absent, 100, 0)]
    public void 迟到分钟只认状态是迟到的记录(AttendanceStatus st, int stored, int expected)
        => Assert.Equal(expected, AttendanceService.EffectiveLateMinutes(Rec(st, stored, 0)));

    [Theory]
    [InlineData(AttendanceStatus.EarlyLeave, 254, 254)]
    [InlineData(AttendanceStatus.Late, 112, 0)]          // 迟到的同一天又早退：状态是迟到，早退不重复计（跟次数口径一致）
    [InlineData(AttendanceStatus.NotPunched, 50, 0)]
    public void 早退分钟只认状态是早退的记录(AttendanceStatus st, int stored, int expected)
        => Assert.Equal(expected, AttendanceService.EffectiveEarlyLeaveMinutes(Rec(st, 0, stored)));

    [Fact]
    public async Task 模板汇总表_梁育雄那种数据_分钟和次数对得上()
    {
        using (var db = CreateContext())
        {
            var user = new User { EmployeeNo = "002000", RealName = "梁育雄", PasswordHash = "x", IsActive = true, HireDate = new DateOnly(2026, 9, 4) };
            db.Users.Add(user);
            db.SaveChanges();
            db.AttendanceRecords.AddRange(
                // 9/7：唯一一次打卡在 22:01，当时被当成上班卡算出 811 分钟迟到，后来状态改成了未打卡
                new AttendanceRecord { UserId = user.Id, WorkDate = new DateOnly(2026, 9, 7), ClockInTime = new DateTime(2026, 9, 7, 22, 1, 0),
                    AttendanceStatus = AttendanceStatus.NotPunched, LateMinutes = 811 },
                // 9/8：唯一一次打卡在 17:30，540 分钟，同样改成了未打卡
                new AttendanceRecord { UserId = user.Id, WorkDate = new DateOnly(2026, 9, 8), ClockInTime = new DateTime(2026, 9, 8, 17, 30, 0),
                    AttendanceStatus = AttendanceStatus.NotPunched, LateMinutes = 540 },
                // 9/14：12:57 才到岗，真正的一次迟到
                new AttendanceRecord { UserId = user.Id, WorkDate = new DateOnly(2026, 9, 14), ClockInTime = new DateTime(2026, 9, 14, 12, 57, 0),
                    ClockOutTime = new DateTime(2026, 9, 14, 21, 0, 0), AttendanceStatus = AttendanceStatus.Late, LateMinutes = 267 });
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var report = await svc.GenerateTemplateReportAsync(new DateOnly(2026, 8, 26), new DateOnly(2026, 9, 25), null);

        var row = Assert.Single(report.Rows, r => r.EmployeeNo == "002000");
        Assert.Equal(1, row.LateCount);
        Assert.Equal(267, row.LateMinutes);    // 以前是 811 + 540 + 267 = 1618
    }

    [Fact]
    public async Task 模板汇总表_迟到又早退的同一天_早退分钟不重复计_和早退次数一致()
    {
        using (var db = CreateContext())
        {
            var user = new User { EmployeeNo = "007634", RealName = "樊孝桥", PasswordHash = "x", IsActive = true, HireDate = new DateOnly(2026, 9, 3) };
            db.Users.Add(user);
            db.SaveChanges();
            db.AttendanceRecords.AddRange(
                new AttendanceRecord { UserId = user.Id, WorkDate = new DateOnly(2026, 9, 10), ClockInTime = new DateTime(2026, 9, 10, 13, 59, 0),
                    ClockOutTime = new DateTime(2026, 9, 10, 15, 37, 0), AttendanceStatus = AttendanceStatus.Late, LateMinutes = 329, EarlyLeaveMinutes = 112 },
                new AttendanceRecord { UserId = user.Id, WorkDate = new DateOnly(2026, 9, 12), ClockInTime = new DateTime(2026, 9, 12, 8, 30, 0),
                    ClockOutTime = new DateTime(2026, 9, 12, 13, 31, 0), AttendanceStatus = AttendanceStatus.EarlyLeave, EarlyLeaveMinutes = 238 });
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var report = await svc.GenerateTemplateReportAsync(new DateOnly(2026, 8, 26), new DateOnly(2026, 9, 25), null);

        var row = Assert.Single(report.Rows, r => r.EmployeeNo == "007634");
        Assert.Equal((1, 329), (row.LateCount, row.LateMinutes));
        Assert.Equal((1, 238), (row.EarlyLeaveCount, row.EarlyLeaveMinutes));   // 以前早退 350 分钟、次数只有 1
    }
}
