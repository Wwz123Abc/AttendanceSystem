using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Services.Implementations;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>加班申请的提交时间窗：当天24点前必须交，不能事后补报；夜班可以提前一天交，白班只能当天交。</summary>
public class OvertimeAdvanceSubmitTests : SqliteTestBase
{
    private static DateTime At(DateOnly d, int h, int m = 0) => d.ToDateTime(new TimeOnly(h, m));

    /// <summary>在 now 这个时刻提交一张加班申请；返回被拦下时的提示（没被时间窗拦下返回 null）。</summary>
    private async Task<string?> SubmitAsync(int uid, DateTime now, DateTime start, DateTime end)
    {
        using var db = CreateContext();
        var svc = new ApprovalService(db, new FakeAttendanceService(), AppOptions, new FixedClock(now));
        try
        {
            await svc.SubmitApprovalAsync(uid, new SubmitApprovalDto
            {
                ApprovalType = ApprovalType.Overtime, Reason = "t", OvertimeStartTime = start, OvertimeEndTime = end
            });
            return null;
        }
        catch (BusinessException ex) { return ex.Message; }
    }

    private static bool IsWindowRejection(string? msg) =>
        msg is not null && (msg.Contains("事后补报") || msg.Contains("提前一天") || msg.Contains("第二天24点"));

    [Fact]
    public async Task 夜班员工前一天就能提交次日的加班()
    {
        var (uid, _) = SeedNightWorld();   // 周一、周二、周三排夜班
        var wed = Tue.AddDays(1);
        // 周二上午提交周三 08:30–11:00 的加班（周三排的是夜班 → 属于夜班）
        var msg = await SubmitAsync(uid, At(Tue, 10), At(wed, 8, 30), At(wed, 11));
        Assert.False(IsWindowRejection(msg), msg);
    }

    [Fact]
    public async Task 夜班当天没排班_看前一天的班次_前一天是夜班就能提前交()
    {
        var (uid, _) = SeedNightWorld();
        var wed = Tue.AddDays(1);
        var thu = wed.AddDays(1);   // 周四没排班，周三是夜班 → 周四算夜班
        var msg = await SubmitAsync(uid, At(wed, 10), At(thu, 20), At(thu.AddDays(1), 2));
        Assert.False(IsWindowRejection(msg), msg);
    }

    [Fact]
    public async Task 白班员工不能提前一天提交()
    {
        var (uid, _) = SeedWeekWorld();   // 周五到周一排白班
        var msg = await SubmitAsync(uid, At(Fri.AddDays(-1), 10), At(Fri, 18), At(Fri, 21));
        Assert.Contains("只有夜班", msg);
    }

    [Fact]
    public async Task 白班员工当天提交当天的加班_不被时间窗拦()
    {
        var (uid, _) = SeedWeekWorld();
        var msg = await SubmitAsync(uid, At(Fri, 10), At(Fri, 18), At(Fri, 21));
        Assert.False(IsWindowRejection(msg), msg);
    }

    [Fact]
    public async Task 加班当天过了不能事后补报_夜班也一样()
    {
        var (uid, _) = SeedNightWorld();
        var wed = Tue.AddDays(1);
        var msg = await SubmitAsync(uid, At(wed, 9), At(Tue, 8, 30), At(Tue, 11));   // 周三提交周二的加班
        Assert.Contains("事后补报", msg);
    }

    [Fact]
    public async Task 夜班最多只能提前一天_提前两天不行()
    {
        var (uid, _) = SeedNightWorld();
        var thu = Tue.AddDays(2);
        var msg = await SubmitAsync(uid, At(Tue, 10), At(thu, 20), At(thu.AddDays(1), 2));
        Assert.Contains("最早只能提前一天", msg);
    }

    [Fact]
    public async Task 加班结束时间最晚只能到开始日期的第二天24点()
    {
        var (uid, _) = SeedNightWorld();
        var wed = Tue.AddDays(1);
        var thu = wed.AddDays(1);
        // 周四 20:00 开始，到周六 00:01 结束，超过周五 24 点
        var msg = await SubmitAsync(uid, At(wed, 10), At(thu, 20), At(thu.AddDays(2), 0, 1));
        Assert.Contains("第二天24点", msg);
        // 正好到周五 24 点（周六 00:00）不算超（但单次时长 > 24h 会被另一条拦下，这里只验证不是"第二天24点"的提示）
        var ok = await SubmitAsync(uid, At(wed, 10), At(thu, 20), At(thu.AddDays(2), 0));
        Assert.DoesNotContain("第二天24点", ok ?? "");
    }

    [Fact]
    public async Task 提前提交的夜班加班_同样不能和夜班的上班时间重叠()
    {
        var (uid, _) = SeedNightWorld();
        var wed = Tue.AddDays(1);
        // 周二提交周三 20:00–23:00 的加班，压在周三夜班（20:00 起）里
        var msg = await SubmitAsync(uid, At(Tue, 10), At(wed, 20), At(wed, 23));
        Assert.Contains("不能和上班时间重叠", msg);
    }
}
