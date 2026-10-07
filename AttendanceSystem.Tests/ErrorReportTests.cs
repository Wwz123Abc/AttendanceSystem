using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AttendanceSystem.Controllers;
using AttendanceSystem.Data;
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
/// 报错带错误编号：格式、不泄露异常原文、编号不重复。
/// </summary>
public class ErrorReportTests : SqliteTestBase
{
    // ── 报错带编号：员工把页面上的整段文字发给开发人员，就能按编号在日志里查到原因 ─────────────

    [Fact]
    public void 错误提示_带错误编号时间和异常类型_不展示异常原文_反馈指引在里面()
    {
        var ex = new InvalidOperationException("Duplicate entry 'x' for key 'IX_User_EmployeeNo'");
        var text = AttendanceSystem.Helpers.ErrorReport.Describe(ex, "保存失败，请稍后重试");

        Assert.StartsWith("保存失败，请稍后重试", text);
        Assert.Matches(@"错误编号 E\d{4}-\d{6}-[0-9A-F]{4}", text);          // 形如 E1006-153012-7F3A
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", text);       // 带时间
        Assert.Contains("InvalidOperationException", text);                       // 带类型名
        Assert.Contains("发给开发人员", text);
        Assert.DoesNotContain("IX_User_EmployeeNo", text);                        // 数据库细节不展示给员工（在日志里按编号查）
    }

    [Fact]
    public void 错误提示_每次生成的编号不一样()
    {
        var ex = new Exception("x");
        var ids = Enumerable.Range(0, 50).Select(_ => AttendanceSystem.Helpers.ErrorReport.NewId()).ToHashSet();
        Assert.True(ids.Count >= 45);   // 同一秒内 4 位随机数偶有重复，但绝大多数不同
        Assert.NotEqual(AttendanceSystem.Helpers.ErrorReport.Describe(ex, "a"), AttendanceSystem.Helpers.ErrorReport.Describe(ex, "a"));
    }
}
