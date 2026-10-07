using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;

namespace AttendanceSystem.Tests;

/// <summary>
/// 远程打卡定位校验：考勤组必须配置定位，以及定位精度容错。
/// </summary>
public class LocationValidationTests : SqliteTestBase
{
    // ── 远程打卡必须先通过定位校验（2026-09-29，用户确认：考勤组没配定位就不能远程打卡，不能是"不限制"）──

    private async Task<(int UserId, int GroupId)> SeedLocationWorldAsync(bool enableLocation, bool withLocation)
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "定位组", EnableLocationPunch = enableLocation };
        db.AttendanceGroups.Add(group);
        await db.SaveChangesAsync();
        if (withLocation)
        {
            db.AttendanceGroupLocations.Add(new AttendanceGroupLocation
            {
                AttendanceGroupId = group.Id, LocationName = "总部", Latitude = 30.0, Longitude = 120.0, RadiusMeters = 200
            });
            await db.SaveChangesAsync();
        }
        var user = new User { EmployeeNo = "LOC1", RealName = "定位员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Id, group.Id);
    }

    [Fact]
    public async Task 定位校验_考勤组没开启定位打卡_远程打卡直接拒绝_不再是不限制()
    {
        var (_, gid) = await SeedLocationWorldAsync(enableLocation: false, withLocation: false);
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var (valid, message) = await svc.ValidateLocationAsync(gid, 30.0, 120.0);
        Assert.False(valid);
        Assert.Contains("未开启或配置定位打卡", message);
    }

    [Fact]
    public async Task 定位校验_考勤组开了定位打卡但没配置打卡地点_同样拒绝()
    {
        var (_, gid) = await SeedLocationWorldAsync(enableLocation: true, withLocation: false);
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var (valid, message) = await svc.ValidateLocationAsync(gid, 30.0, 120.0);
        Assert.False(valid);
        Assert.Contains("未开启或配置定位打卡", message);
    }

    [Fact]
    public async Task 定位校验_没有分配考勤组_远程打卡直接拒绝()
    {
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var (valid, message) = await svc.ValidateLocationAsync(null, 30.0, 120.0);
        Assert.False(valid);
        Assert.Contains("未分配考勤组", message);
    }

    [Fact]
    public async Task 定位校验_考勤组配好了定位_人在范围内正常通过()
    {
        var (_, gid) = await SeedLocationWorldAsync(enableLocation: true, withLocation: true);
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var (valid, message) = await svc.ValidateLocationAsync(gid, 30.0, 120.0);
        Assert.True(valid, message);
    }

    [Fact]
    public async Task 定位校验_考勤组配好了定位_人在范围外仍然拒绝()
    {
        var (_, gid) = await SeedLocationWorldAsync(enableLocation: true, withLocation: true);
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var (valid, message) = await svc.ValidateLocationAsync(gid, 31.0, 120.0);   // 差 1 度纬度，约 111 公里外
        Assert.False(valid);
        Assert.Contains("超出有效范围", message);
    }

    // ── 定位校验：精度容错分支（§6 第 1 条：以前 5 条用例全走三参重载，accuracy 恒为 null）──────────

    private async Task<int> SeedLocationGroupAsync()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "定位组", EnableLocationPunch = true };
        db.AttendanceGroups.Add(group);
        await db.SaveChangesAsync();
        db.AttendanceGroupLocations.Add(new AttendanceGroupLocation
        {
            AttendanceGroupId = group.Id, LocationName = "总部", Latitude = 30.0, Longitude = 120.0, RadiusMeters = 200
        });
        await db.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task 定位校验_圈外233米_精度50米_把误差算进去后放行_精度20米仍拒绝()
    {
        var gid = await SeedLocationGroupAsync();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var lat = 30.0021;   // 约 233 米，圈半径 200 米

        Assert.True((await svc.ValidateLocationAsync(gid, lat, 120.0, 50)).Valid);      // 233 − 50 = 183 ≤ 200
        var tight = await svc.ValidateLocationAsync(gid, lat, 120.0, 20);               // 233 − 20 = 213 > 200
        Assert.False(tight.Valid);
        Assert.Contains("超出有效范围", tight.Message);
        Assert.False((await svc.ValidateLocationAsync(gid, lat, 120.0, null)).Valid);   // 没传精度：不给容错
    }

    [Fact]
    public async Task 定位校验_浏览器报的精度再大也最多只给100米容错_不能把半径放大到想多远就多远()
    {
        var gid = await SeedLocationGroupAsync();
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var lat = 30.0045;   // 约 500 米

        Assert.False((await svc.ValidateLocationAsync(gid, lat, 120.0, 1000)).Valid);   // 封顶 100：500 − 100 = 400 > 200
        Assert.False((await svc.ValidateLocationAsync(gid, lat, 120.0, 100000)).Valid);
    }

    [Fact]
    public void HaversineMeters_纬度差1度约111公里_同一点为0()
    {
        Assert.Equal(111195, AttendanceService.HaversineMeters(30, 120, 31, 120), 0.01 * 111195);
        Assert.Equal(0, AttendanceService.HaversineMeters(30, 120, 30, 120), 1e-6);
    }
}
