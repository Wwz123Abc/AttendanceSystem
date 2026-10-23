using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Helpers;
using AttendanceSystem.Services.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 新能源-客服白班加班补偿（2026-10-23）：晚饭段 17:00–18:00 把休息日白班加班多扣的 0.5 小时，在发薪汇总报表里加回来；
/// 库里存的值已经够了（按旧饭点算的）就不再补（9/26、9/27、10/3、10/4 数据核对发现补多了）。
/// </summary>
public class DinnerCompensationTests : SqliteTestBase
{
    private async Task<decimal> RunAsync(string groupName, string shiftName, decimal storedOt,
        int otEndHour = 17, int otEndMinute = 30, bool dinnerFrom1730 = false)
    {
        UnpaidBreakSettings.ResetAll();
        int gid, uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = groupName };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            gid = group.Id;
            var shift = DayShift(); shift.AttendanceGroupId = gid; shift.ShiftName = shiftName;
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "C1", RealName = "补偿员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = gid, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            uid = user.Id;
            var otEnd = Sun.ToDateTime(new TimeOnly(otEndHour, otEndMinute));
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = uid, WorkDate = Sun, ShiftScheduleId = shift.Id });   // 排的班自己配了周日休息
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = Sun, AttendanceStatus = AttendanceStatus.Normal,
                ClockInTime = Sun.ToDateTime(new TimeOnly(8, 30)), ClockOutTime = otEnd, OvertimeHours = storedOt
            });
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "JB-C-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Approved,
                OvertimeStartTime = Sun.ToDateTime(new TimeOnly(8, 30)), OvertimeEndTime = otEnd, OvertimeDurationHours = storedOt, Reason = "t"
            });
            db.SaveChanges();
        }
        // 新能源-客服的饭点：午饭 12:00–13:00、晚饭 17:00–18:00（线上 2026-10-23 的实际配置）
        UnpaidBreakSettings.Apply(gid, dinnerFrom1730
            ? [(new TimeOnly(12, 0), new TimeOnly(13, 0)), (new TimeOnly(17, 30), new TimeOnly(18, 30))]
            : [(new TimeOnly(12, 0), new TimeOnly(13, 0)), (new TimeOnly(17, 0), new TimeOnly(18, 0))]);
        try
        {
            using var db = CreateContext();
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var row = (await svc.GenerateTemplateReportAsync(Sun, Sun, null)).Rows.Single();
            Assert.Equal(row.TotalOvertimeHours, row.RestDayOvertimeHours + row.WeekdayOvertimeHours);
            return row.TotalOvertimeHours;
        }
        finally { UnpaidBreakSettings.ResetAll(); }
    }

    [Fact]
    public async Task 新能源客服白班_休息日加班_按晚饭段算少了半小时_加回来()
        => Assert.Equal(8m, await RunAsync("新能源-客服", "白班", storedOt: 7.5m));   // 10/3、10/4 的 08:30–17:30 存 7.5

    [Fact]
    public async Task 库里已经是8_不再补()
        => Assert.Equal(8m, await RunAsync("新能源-客服", "白班", storedOt: 8m));   // 9/26、9/27 的 08:30–17:30 存的就是 8

    [Fact]
    public async Task 整段跨过晚饭的长加班_存的值正确_不补()
        => Assert.Equal(10m, await RunAsync("新能源-客服", "白班", storedOt: 10m, otEndHour: 20, otEndMinute: 30));   // 10/3、10/4 的 08:30–20:30 存 10（扣了午饭+晚饭各 1 小时）

    [Fact]
    public async Task 库里按旧饭点算的长加班_本来就多了半小时_不补()
        => Assert.Equal(10.5m, await RunAsync("新能源-客服", "白班", storedOt: 10.5m, otEndHour: 20, otEndMinute: 30));   // 9/26、9/27 的 08:30–20:30 存 10.5

    [Fact]
    public async Task 新能源客服_其他班次不补偿()
        => Assert.Equal(7.5m, await RunAsync("新能源-客服", "中班", storedOt: 7.5m));

    [Fact]
    public async Task 其他考勤组的白班不补偿()
        => Assert.Equal(7.5m, await RunAsync("别的组", "白班", storedOt: 7.5m));

    [Fact]
    public async Task 晚饭段改成17点半起之后_补偿自动变成0_不会重复加()
        => Assert.Equal(7.5m, await RunAsync("新能源-客服", "白班", storedOt: 7.5m, dinnerFrom1730: true));   // 饭点改了以后新算出来的加班本来就是 8，不会再叠加一次
}
