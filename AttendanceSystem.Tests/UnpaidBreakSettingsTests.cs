using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>饭点时段是按考勤组的全局静态设置：这组测试会改它，必须和别的测试错开跑（DisableParallelization），并且每条都恢复默认。</summary>
[CollectionDefinition("UnpaidBreakStatic", DisableParallelization = true)]
public class UnpaidBreakStaticCollection;

[Collection("UnpaidBreakStatic")]
public class UnpaidBreakSettingsTests : SqliteTestBase, IDisposable
{
    public new void Dispose()
    {
        UnpaidBreakSettings.ResetAll();
        base.Dispose();
    }

    private static DateTime T(int day, int h, int m) => new DateTime(2026, 10, day, h, m, 0);
    private static TimeOnly H(int h, int m = 0) => new(h, m);

    [Fact]
    public void 默认四段饭点_压到的部分才扣_按半小时向下取整_没有考勤组也用默认()
    {
        Assert.Equal("00:00-01:00,05:30-06:00,12:00-13:00,17:30-18:00", UnpaidBreakSettings.Format(UnpaidBreakSettings.WindowsFor(null)));
        Assert.Equal(7.5m, AttendanceService.ComputeWorkHours(T(5, 8, 30), T(5, 17, 0)));    // 8.5 小时扣中午 1 小时
        Assert.Equal(4.5m, AttendanceService.ComputeWorkHours(T(5, 13, 0), T(5, 17, 30)));   // 没压到饭点
        Assert.Equal(0m, AttendanceService.ComputeWorkHours(T(5, 12, 10), T(5, 12, 50)));    // 整段都在饭点里
    }

    [Fact]
    public void 只改一个考勤组_别的考勤组和没有考勤组的人不受影响()
    {
        // 考勤组 3：晚餐改成 17:30-18:30，其余照旧
        UnpaidBreakSettings.Apply(3, [(H(0), H(1)), (H(5, 30), H(6)), (H(12), H(13)), (H(17, 30), H(18, 30))]);
        Assert.True(UnpaidBreakSettings.IsCustomized(3));
        Assert.False(UnpaidBreakSettings.IsCustomized(4));

        // 17:00-19:00 在岗 2 小时：考勤组 3 扣 17:30-18:30 一小时 → 1.0；默认只扣 17:30-18:00 半小时 → 1.5
        Assert.Equal(1.0m, AttendanceService.ComputeWorkHours(T(5, 17, 0), T(5, 19, 0), 3));
        Assert.Equal(1.5m, AttendanceService.ComputeWorkHours(T(5, 17, 0), T(5, 19, 0), 4));
        Assert.Equal(1.5m, AttendanceService.ComputeWorkHours(T(5, 17, 0), T(5, 19, 0)));
        Assert.Equal(1.5m, AttendanceService.ComputeWorkHours(T(5, 17, 0), T(5, 19, 0), null));

        // 恢复默认
        UnpaidBreakSettings.Remove(3);
        Assert.Equal(1.5m, AttendanceService.ComputeWorkHours(T(5, 17, 0), T(5, 19, 0), 3));
        // 保存成和默认一模一样，等于没有单独设置
        UnpaidBreakSettings.Apply(3, [.. UnpaidBreakSettings.Defaults]);
        Assert.False(UnpaidBreakSettings.IsCustomized(3));
    }

    [Fact]
    public void 请假时长也按考勤组的饭点扣()
    {
        UnpaidBreakSettings.Apply(3, [(H(11, 30), H(12, 30))]);   // 考勤组 3 只有 11:30-12:30 一段饭点
        var shift = DayShift();
        var d = DateOnly.FromDateTime(T(5, 0, 0));
        // 请假 12:30-15:30 共 3 小时：考勤组 3 的饭点到 12:30 就结束，不扣 → 3.0；默认饭点 12:00-13:00 压到 30 分钟 → 2.5
        Assert.Equal(3.0m, AttendanceService.ComputeLeaveHoursForDay(d, T(5, 12, 30), T(5, 15, 30), 8, shift, 3));
        Assert.Equal(2.5m, AttendanceService.ComputeLeaveHoursForDay(d, T(5, 12, 30), T(5, 15, 30), 8, shift, 4));
        Assert.Equal(2.5m, AttendanceService.ComputeLeaveHoursForDay(d, T(5, 12, 30), T(5, 15, 30), 8, shift));
    }

    [Fact]
    public void 校验_结束不晚于开始_重叠_太长_太多_都拒绝_合法的通过()
    {
        Assert.Null(UnpaidBreakSettings.Validate([(H(12), H(13)), (H(17, 30), H(18))]));
        Assert.NotNull(UnpaidBreakSettings.Validate([(H(13), H(12))]));                        // 结束早于开始
        Assert.NotNull(UnpaidBreakSettings.Validate([(H(12), H(12))]));                        // 零长度
        Assert.NotNull(UnpaidBreakSettings.Validate([(H(12), H(13)), (H(12, 30), H(14))]));   // 重叠
        Assert.NotNull(UnpaidBreakSettings.Validate([(H(10), H(15))]));                        // 超过 4 小时
        Assert.NotNull(UnpaidBreakSettings.Validate(Enumerable.Range(0, 9).Select(i => (H(i * 2), H(i * 2, 30))).ToList()));   // 超过 8 段
        Assert.Null(UnpaidBreakSettings.Validate([]));                                         // 一段都没有也允许
    }

    [Fact]
    public void 存库格式_解析和格式化互逆_乱格式返回空()
    {
        var parsed = UnpaidBreakSettings.TryParse("17:30-18:00, 12:00-13:00");
        Assert.NotNull(parsed);
        Assert.Equal("12:00-13:00,17:30-18:00", UnpaidBreakSettings.Format(parsed!));          // 格式化时按开始时间排序
        Assert.Empty(UnpaidBreakSettings.TryParse("")!);
        Assert.Null(UnpaidBreakSettings.TryParse("12点-13点"));
        Assert.Null(UnpaidBreakSettings.TryParse("12:00"));
    }

    [Fact]
    public async Task 启动时读库_每个考勤组一行_读不懂的行跳过_那个组用默认()
    {
        using (var db = CreateContext())
        {
            db.SystemSettings.AddRange(
                new SystemSetting { Key = UnpaidBreakSettings.KeyFor(3), Value = "12:00-13:00,17:30-18:30,00:00-01:00,05:30-06:00" },
                new SystemSetting { Key = UnpaidBreakSettings.KeyFor(4), Value = "乱写的" },
                new SystemSetting { Key = UnpaidBreakSettings.KeyFor(5), Value = "12:00-13:00,17:30-18:00,00:00-01:00,05:30-06:00" },   // 和默认一样
                new SystemSetting { Key = "别的设置", Value = "x" });
            await db.SaveChangesAsync();
        }
        using (var db = CreateContext())
            await UnpaidBreakSettings.LoadAsync(db);

        Assert.True(UnpaidBreakSettings.IsCustomized(3));
        Assert.False(UnpaidBreakSettings.IsCustomized(4));
        Assert.False(UnpaidBreakSettings.IsCustomized(5));
        Assert.Equal("00:00-01:00,05:30-06:00,12:00-13:00,17:30-18:30", UnpaidBreakSettings.Format(UnpaidBreakSettings.WindowsFor(3)));
        Assert.Equal(4, UnpaidBreakSettings.WindowsFor(4).Count);
    }

    [Fact]
    public async Task 加班审批通过回写_按申请人所在考勤组的饭点扣()
    {
        var (uid, groupId) = SeedWeekWorld();
        UnpaidBreakSettings.Apply(groupId, [(H(0), H(1)), (H(5, 30), H(6)), (H(12), H(13)), (H(17, 30), H(18, 30))]);
        var start = Mon.ToDateTime(new TimeOnly(15, 0));
        var end   = Mon.ToDateTime(new TimeOnly(19, 0));   // 4 小时；默认扣 17:30-18:00 → 3.5；这个考勤组扣 17:30-18:30 → 3.0
        int id;
        using (var db = CreateContext())
        {
            var req = new ApprovalRequest
            {
                RequestNo = "JB-GRP-1", ApplicantUserId = uid, ApprovalType = ApprovalType.Overtime, ApprovalStatus = ApprovalStatus.Approved,
                OvertimeStartTime = start, OvertimeEndTime = end, OvertimeDurationHours = 4, Reason = "t"
            };
            db.ApprovalRequests.Add(req);
            await db.SaveChangesAsync();
            id = req.Id;
        }
        using (var db = CreateContext())
            await new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance).UpdateAttendanceAfterApprovalAsync(id);
        using var check = CreateContext();
        Assert.Equal(3.0m, (await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon)).OvertimeHours);

        // 同样的起止时间按默认饭点算应该是 3.5
        UnpaidBreakSettings.Remove(groupId);
        Assert.Equal(3.5m, AttendanceService.ComputeWorkHours(start, end, groupId));
    }
}
