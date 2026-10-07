using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
/// 迁移脚本 migrate.sql 必须覆盖 Migrations 目录里的全部迁移。
/// </summary>
public class MigrationScriptTests : SqliteTestBase
{
    // ── H1：migrate.sql 必须覆盖 Migrations 目录里的全部迁移 ─────────────────────────

    /// <summary>仓库根目录：从测试程序所在目录往上找，第一个同时含 migrate.sql 和 Migrations 的目录。
    /// 不用 [CallerFilePath]——CI 里开了"可复现构建"（ContinuousIntegrationBuild）后，源码路径会被映射成 "/_/..."，
    /// 编译进去的路径在运行时根本不存在。</summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // 先从测试程序所在目录往上找（CI 里有效）；找不到再退回编译时记录的源码路径（本机从别处运行测试时有效）
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) ?? "" })
            for (var dir = start.Length > 0 && Directory.Exists(start) ? new DirectoryInfo(start) : null; dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "migrate.sql")) && Directory.Exists(Path.Combine(dir.FullName, "Migrations")))
                    return dir.FullName;
        throw new DirectoryNotFoundException("找不到包含 migrate.sql 和 Migrations 的仓库根目录");
    }

    [Fact]
    public void 迁移脚本migrate_sql_覆盖Migrations目录里的全部迁移()
    {
        // 生产建库/升级靠手动执行 migrate.sql；以前最后一条迁移（AddAnnouncementScopeRoles）没生成进脚本，
        // 用脚本建/升的库公告相关页面会整片 500（Unknown column 'Announcement.ScopeRoles'）
        var root = RepoRoot();
        var inCode = Directory.GetFiles(Path.Combine(root, "Migrations"), "*.cs")
            .Select(Path.GetFileName).Select(f => f!)
            .Where(f => !f.EndsWith(".Designer.cs") && !f.EndsWith("ModelSnapshot.cs"))
            .Select(f => f[..^3]).ToHashSet();
        var sql = File.ReadAllText(Path.Combine(root, "migrate.sql"));
        var inScript = Regex.Matches(sql, @"`MigrationId` = '(\d{14}_\w+)'").Select(m => m.Groups[1].Value).ToHashSet();

        Assert.NotEmpty(inCode);
        Assert.Empty(inCode.Except(inScript));    // 脚本里缺的迁移
        Assert.Empty(inScript.Except(inCode));    // 脚本里多出来、代码里没有的
    }
}
