using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Pages.Approval;
using AttendanceSystem.Services.BackgroundServices;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NPOI.XSSF.UserModel;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 测试基类：每个测试实例用一个独立的 SQLite 内存库（建好全部表），测试结束自动释放；
/// 并提供几个跨测试类共用的小工具（造用户、造班次、固定的几个日期、造 CurrentUser）。
/// 以前这段样板代码在每个测试类里各抄了一份。
/// </summary>
public abstract class SqliteTestBase : IDisposable
{
    protected readonly SqliteConnection _connection;
    protected static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());

    protected static readonly DateOnly Fri = new(2026, 9, 4);   // 周五
    protected static readonly DateOnly Sat = new(2026, 9, 5);
    protected static readonly DateOnly Sun = new(2026, 9, 6);
    protected static readonly DateOnly Mon = new(2026, 9, 7);
    protected static readonly DateOnly Tue = new(2026, 9, 8);   // 周二，普通工作日

    protected SqliteTestBase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    protected AttendanceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options);

    /// <summary>造一个最简单的在职员工（不入库）。</summary>
    protected static User U(string no, string name) => new()
    {
        EmployeeNo = no, RealName = name, PasswordHash = "x", IsActive = true, HireDate = new DateOnly(2026, 1, 1)
    };

    protected static CurrentUser Cu(UserRole role, int? scopedDept) => new() { UserId = 1, Role = role, ScopedDepartmentId = scopedDept };

    /// <summary>白班 08:30-17:30，默认休息日是周六周日；cross=true 时标成跨天班次。</summary>
    protected static ShiftSchedule DayShift(bool cross = false) => new()
    {
        ShiftName = "白班", WorkStartTime = new TimeOnly(8, 30), WorkEndTime = new TimeOnly(17, 30),
        LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, IsCrossDay = cross, StandardWorkHours = 8
    };

    /// <summary>夜班 20:00-次日 08:00，没有固定休息日。</summary>
    protected static ShiftSchedule NightShift() => new()
    {
        ShiftName = "夜班", WorkStartTime = new TimeOnly(20, 0), WorkEndTime = new TimeOnly(8, 0), IsCrossDay = true,
        LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 11, RestDaysOfWeek = ""
    };

    protected (int userId, int groupId) SeedWeekWorld()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = DayShift(); shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "L1", RealName = "员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        db.SaveChanges();
        foreach (var d in new[] { Fri, Sat, Sun, Mon })   // 周末也排班（周末只是班次配置的休息日）
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return (user.Id, group.Id);
    }

    protected (int userId, int shiftId) SeedNightWorld()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "夜班组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = NightShift(); shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "N1", RealName = "夜班员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        var dev = new ZKDevice { SN = "SNN", IsActive = true };
        db.ZKDevices.Add(dev);
        db.SaveChanges();
        db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
        foreach (var d in new[] { Mon, Tue, Tue.AddDays(1) })
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
        // 周一晚上 20:00 上班，还没打下班卡
        db.AttendanceRecords.Add(new AttendanceRecord { UserId = user.Id, WorkDate = Mon, ClockInTime = Mon.ToDateTime(new TimeOnly(20, 0)), AttendanceStatus = AttendanceStatus.Normal });
        db.SaveChanges();
        return (user.Id, shift.Id);
    }

    protected async Task<int> AddApprovedAsync(ApprovalRequest r)
    {
        using var db = CreateContext();
        r.ApprovalStatus = ApprovalStatus.Approved;
        db.ApprovalRequests.Add(r);
        await db.SaveChangesAsync();
        return r.Id;
    }

    protected (int applicant, int supervisor, int groupApprover, int fallbackAdmin) SeedApprovalWorld(ApprovalLevelType level, bool withGroupApprovers)
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "测试组", ApprovalLevel = level };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var supervisor = new User { EmployeeNo = "S01", RealName = "直属上级", PasswordHash = "x", Role = UserRole.Supervisor, AttendanceGroupId = group.Id };
        var groupApprover = new User { EmployeeNo = "G01", RealName = "组审批人", PasswordHash = "x", Role = UserRole.TeamLeader, AttendanceGroupId = group.Id };
        var admin = new User { EmployeeNo = "A01", RealName = "兜底管理员", PasswordHash = "x", Role = UserRole.Admin };   // 不带范围 = 总部
        db.Users.AddRange(supervisor, groupApprover, admin);
        db.SaveChanges();

        var applicant = new User { EmployeeNo = "E01", RealName = "申请人", PasswordHash = "x", AttendanceGroupId = group.Id, SupervisorUserId = supervisor.Id };
        db.Users.Add(applicant);
        db.SaveChanges();

        if (withGroupApprovers)
        {
            db.AttendanceGroupApprovers.Add(new AttendanceGroupApprover { AttendanceGroupId = group.Id, UserId = groupApprover.Id });
            db.SaveChanges();
        }
        return (applicant.Id, supervisor.Id, groupApprover.Id, admin.Id);
    }

    protected (UserService Svc, int AdminId) NewUserService(AttendanceDbContext db)
    {
        var admin = U("ADM1", "操作管理员");
        admin.Role = UserRole.Admin;
        db.Users.Add(admin);
        db.SaveChanges();
        var scope = new DeptScopeService(db);
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var zk = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        return (new UserService(db, zk, AppOptions, scope, NullLogger<UserService>.Instance), admin.Id);
    }

    protected (int userId, int supervisorId) SeedNightWorldForFutureCheck(DateOnly shiftDate)
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "夜班组-未来校验" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = NightShift(); shift.AttendanceGroupId = group.Id;   // 20:00-08:00 跨天
        db.ShiftSchedules.Add(shift);
        var supervisor = new User { EmployeeNo = "SUPF", RealName = "上级", PasswordHash = "x", IsActive = true, Role = UserRole.Supervisor };
        db.Users.Add(supervisor);
        db.SaveChanges();
        var user = new User
        {
            EmployeeNo = "NF1", RealName = "夜班员工-未来校验", PasswordHash = "x", IsActive = true,
            AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1), SupervisorUserId = supervisor.Id
        };
        db.Users.Add(user);
        db.SaveChanges();
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = shiftDate, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return (user.Id, supervisor.Id);
    }

    protected async Task<Exception?> TrySubmitOvertimeAsync(int uid, TimeOnly start, TimeOnly end)
    {
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions);
        var today = DateOnly.FromDateTime(DateTime.Today);
        try
        {
            await svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
            {
                ApprovalType = ApprovalType.Overtime, Reason = "t",
                OvertimeStartTime = today.ToDateTime(start), OvertimeEndTime = today.ToDateTime(end)
            });
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    /// <summary>造一个"今天排了白班 08:30-17:30"的员工（有直属上级，提交才走得通）；restDays 是这个班次的每周休息日。</summary>
    protected int SeedOvertimeWorld(string restDays, ShiftSchedule? shiftOverride = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "加班组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = shiftOverride ?? DayShift();
        shift.AttendanceGroupId = group.Id; shift.RestDaysOfWeek = restDays;
        db.ShiftSchedules.Add(shift);
        var boss = new User { EmployeeNo = "B1", RealName = "上级", PasswordHash = "x", IsActive = true, Role = UserRole.Supervisor };
        db.Users.Add(boss);
        db.SaveChanges();
        var user = new User { EmployeeNo = "OT1", RealName = "加班员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, SupervisorUserId = boss.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        db.SaveChanges();
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = today, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return user.Id;
    }

    protected async Task SyncDayAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await svc.ProcessAttLogAsync("SND", times.Select(t => new ZKAttLogRow("D1", t, 0, 15)).ToList());
    }

    protected (int userId, int shiftId) SeedDayMidWorld()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "午卡组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = DayShift(); shift.AttendanceGroupId = group.Id;
        shift.MidCheckWindows = "12:00-13:00";
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "M1", RealName = "午卡员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        var dev = new ZKDevice { SN = "SNM", IsActive = true };
        db.ZKDevices.Add(dev);
        db.SaveChanges();
        db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = Tue, ShiftScheduleId = shift.Id });
        db.SaveChanges();
        return (user.Id, shift.Id);
    }

    protected int SeedDayDeviceWorld()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "白班设备组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();
        var shift = DayShift(); shift.AttendanceGroupId = group.Id;   // 08:30-17:30，周日周六休息
        db.ShiftSchedules.Add(shift);
        var user = new User { EmployeeNo = "D1", RealName = "白班员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        var dev = new ZKDevice { SN = "SND", IsActive = true };
        db.ZKDevices.Add(dev);
        db.SaveChanges();
        db.UserZKDevices.Add(new UserZKDevice { UserId = user.Id, ZKDeviceId = dev.Id });
        foreach (var d in new[] { Mon, Tue })
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
        // 周一 08:25 上班，加班中，还没下班
        db.AttendanceRecords.Add(new AttendanceRecord { UserId = user.Id, WorkDate = Mon, ClockInTime = Mon.ToDateTime(new TimeOnly(8, 25)), AttendanceStatus = AttendanceStatus.Normal });
        db.SaveChanges();
        return user.Id;
    }
}
