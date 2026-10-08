using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using AttendanceSystem.Models.Exceptions;

namespace AttendanceSystem.Tests;

/// <summary>
/// 员工删除保护：有考勤/打卡/申请历史的员工只能停用，不能物理删除。
/// </summary>
public class UserDeletionGuardTests : SqliteTestBase
{
    // ── M14：有考勤/打卡/申请历史的员工只能停用，不能物理删除 ────────────────────────────

    [Fact]
    public async Task 删除员工_没有任何历史数据的空账号_可以删()
    {
        using var db = CreateContext();
        var (svc, adminId) = NewUserService(db);
        var empty = U("EMP0", "误建的空账号");
        db.Users.Add(empty);
        await db.SaveChangesAsync();

        Assert.True(await svc.DeleteUserAsync(empty.Id, adminId));

        using var check = CreateContext();
        Assert.False(await check.Users.AnyAsync(u => u.Id == empty.Id));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("punch")]
    [InlineData("request")]
    public async Task 删除员工_有考勤记录或打卡流水或申请单_只能停用_账号和历史数据都保留(string kind)
    {
        int uid;
        using (var db = CreateContext())
        {
            var u = U("EMP-" + kind, "有历史的员工");
            db.Users.Add(u);
            await db.SaveChangesAsync();
            uid = u.Id;
            if (kind == "record")
                db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = new DateOnly(2026, 9, 10) });
            else if (kind == "punch")
                db.AttendancePunches.Add(new AttendancePunch { UserId = uid, PunchTime = new DateTime(2026, 9, 10, 8, 30, 0), PunchType = PunchType.ClockIn, IsValid = true });
            else
                db.ApprovalRequests.Add(new ApprovalRequest { RequestNo = "QJ-DEL-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Leave });
            await db.SaveChangesAsync();
        }

        using var db2 = CreateContext();
        var (svc, adminId) = NewUserService(db2);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => svc.DeleteUserAsync(uid, adminId));
        Assert.Contains("停用", ex.Message);

        using var check = CreateContext();
        Assert.True(await check.Users.AnyAsync(u => u.Id == uid));                       // 人还在
        Assert.Equal(kind == "record" ? 1 : 0, await check.AttendanceRecords.CountAsync(r => r.UserId == uid));   // 历史没被级联清掉
        Assert.Equal(kind == "punch" ? 1 : 0, await check.AttendancePunches.CountAsync(p => p.UserId == uid));
        Assert.Equal(kind == "request" ? 1 : 0, await check.ApprovalRequests.CountAsync(a => a.ApplicantUserId == uid));
    }
}
