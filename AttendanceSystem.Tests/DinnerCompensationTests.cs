using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Helpers;
using AttendanceSystem.Services.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>新能源-客服白班加班补偿（2026-10-23）：晚饭段 17:00–18:00 把休息日白班加班多扣的 0.5 小时，在发薪汇总报表里加回来。</summary>
public class DinnerCompensationTests : SqliteTestBase
{
    private async Task<(decimal Total, decimal RestDay, decimal WeekDay)> RunAsync(string groupName, string shiftName, bool dinnerFrom1730 = false)
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
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = uid, WorkDate = Sun, ShiftScheduleId = shift.Id });   // 排的班自己配了周日休息
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = uid, WorkDate = Sun, AttendanceStatus = AttendanceStatus.Normal,
                ClockInTime = Sun.ToDateTime(new TimeOnly(8, 30)), ClockOutTime = Sun.ToDateTime(new TimeOnly(17, 30)), OvertimeHours = 7.5m
            });
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "JB-C-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Approved,
                OvertimeStartTime = Sun.ToDateTime(new TimeOnly(8, 30)), OvertimeEndTime = Sun.ToDateTime(new TimeOnly(17, 30)), OvertimeDurationHours = 7.5m, Reason = "t"
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
            return (row.TotalOvertimeHours, row.RestDayOvertimeHours, row.WeekdayOvertimeHours);
        }
        finally { UnpaidBreakSettings.ResetAll(); }
    }

    [Fact]
    public async Task 新能源客服白班_休息日加班_晚饭段多扣的半小时加回来()
    {
        var (total, rest, _) = await RunAsync("新能源-客服", "白班");
        Assert.Equal(8m, total);
        Assert.Equal(8m, rest);
    }

    [Fact]
    public async Task 新能源客服_其他班次不补偿()
    {
        var (total, _, _) = await RunAsync("新能源-客服", "中班");
        Assert.Equal(7.5m, total);
    }

    [Fact]
    public async Task 其他考勤组的白班不补偿()
    {
        var (total, _, _) = await RunAsync("别的组", "白班");
        Assert.Equal(7.5m, total);
    }

    [Fact]
    public async Task 晚饭段改成17点半起之后_补偿自动变成0_不会重复加()
    {
        var (total, _, _) = await RunAsync("新能源-客服", "白班", dinnerFrom1730: true);
        Assert.Equal(7.5m, total);   // 库里存的 7.5 不变，补偿为 0：饭点改了以后新算出来的加班本来就是 8，不会再叠加一次
    }
}
