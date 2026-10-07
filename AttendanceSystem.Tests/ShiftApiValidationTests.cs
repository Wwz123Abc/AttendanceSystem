using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 班次接口的字段范围校验。
/// </summary>
public class ShiftApiValidationTests : SqliteTestBase
{
    // ── M6：班次 API 的字段范围校验 ───────────────────────────────────────────────

    private static string? ValidateShift(string? name = "白班", int late = 5, int early = 5, int ot = 30, int earliest = 60,
        decimal std = 8, string? color = "#1890ff", string? rest = "0,6", string? mid = null)
        => (string?)typeof(AdminController).GetMethod("ValidateShiftFields", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [name, late, early, ot, earliest, std, color, rest, mid]);

    [Fact]
    public void 班次接口校验_合法配置通过_越界的各字段都被拒绝()
    {
        Assert.Null(ValidateShift());
        Assert.Null(ValidateShift(rest: ""));                       // 一个休息日都不配是允许的
        Assert.Contains("名称", ValidateShift(name: " "));
        Assert.Contains("迟到容忍", ValidateShift(late: 999));
        Assert.Contains("早退容忍", ValidateShift(early: -1));
        Assert.Contains("加班判定", ValidateShift(ot: 500));
        Assert.Contains("标准工时", ValidateShift(std: 0));
        Assert.Contains("标准工时", ValidateShift(std: 100));
        Assert.Contains("颜色", ValidateShift(color: "red"));
        Assert.Contains("休息日", ValidateShift(rest: "0,9"));
        Assert.Contains("过长", ValidateShift(mid: new string('x', 501)));
    }
}
