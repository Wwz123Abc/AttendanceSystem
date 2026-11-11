using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 迟到满 4 小时按半天旷工（2026-11-09 起）：**考勤机同步路径也要套**，不能只有手机打卡套。
/// 2026-11-10 发现刘金城 11-09 用考勤机打卡（12:49–20:00、迟到 259 分钟）算出 4.5 小时，
/// 而按口径应该是半天 4 小时——设备同步那条路径漏了这个封顶。
/// </summary>
public class HalfDayAbsentDeviceSyncTests : SqliteTestBase
{
    private static readonly DateOnly RuleDay = new(2026, 11, 9);   // 规则生效日

    private int SeedDeviceEmployee()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "设备半天旷工组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var shift = DayShift();                 // 08:30–17:30，标准工时 8
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);

        var user = new User { EmployeeNo = "HDA9", RealName = "设备半天旷工", PasswordHash = "x", IsActive = true,
            AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        var dev = new ZKDevice { SN = "SNHDA9", IsActive = true };
        db.ZKDevices.Add(dev);
        db.SaveChanges();
        db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = RuleDay, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return user.Id;
    }

    [Fact]
    public async Task 考勤机打卡_迟到满4小时_工时也封顶半天()
    {
        var uid = SeedDeviceEmployee();
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var sync = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);

        await sync.ProcessAttLogAsync("SNHDA9", [
            new ZKAttLogRow("HDA9", RuleDay.ToDateTime(new TimeOnly(12, 49)), 0, 15),
            new ZKAttLogRow("HDA9", RuleDay.ToDateTime(new TimeOnly(20, 0)),  0, 15)
        ]);

        db.ChangeTracker.Clear();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == RuleDay);
        Assert.Equal(AttendanceStatus.Late, rec.AttendanceStatus);
        Assert.True(rec.LateMinutes >= 240);
        Assert.Equal(4m, rec.ActualWorkHours);      // 半天封顶（8 小时班 → 4 小时），不是 4.5
    }
}
