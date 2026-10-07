using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 夜班续接：下班卡接到哪一天、续接时间窗、重复刷卡、一次卡都没打的晚上班卡、夜班上班卡合理性。
/// </summary>
public class NightShiftContinuationTests : SqliteTestBase
{
    // ── ② 夜班续接时间窗 ─────────────────────────────────────────────

    private async Task SyncAsync(params DateTime[] times)
    {
        using var db = CreateContext();
        var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var svc = new ZKDeviceSyncService(db, NullLogger<ZKDeviceSyncService>.Instance, AppOptions, att);
        await svc.ProcessAttLogAsync("SNN", times.Select(t => new ZKAttLogRow("N1", t, 0, 15)).ToList());
    }

    [Fact]
    public async Task 夜班_周二早上的下班卡_仍然接到周一那条记录()
    {
        var (uid, _) = SeedNightWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 10)));

        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(8, 10)), mon.ClockOutTime);
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
    }

    [Fact]
    public async Task 夜班_周一漏打下班卡_周二晚上的上班卡不再被当成周一的下班卡()
    {
        var (uid, _) = SeedNightWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(20, 5)));   // 离周一应下班（周二 08:00）已经 12 个小时

        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Null(mon.ClockOutTime);                           // 以前这里会被填上 20:05
        var tue = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(20, 5)), tue.ClockInTime);   // 周二自己的上班卡
    }

    [Fact]
    public async Task 夜班_下班卡打完几分钟内又刷一次_仍归周一_不会凭空多出周二的上班卡()
    {
        var (uid, _) = SeedNightWorld();
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 0)));     // 第一次：下班
        await SyncAsync(Tue.ToDateTime(new TimeOnly(8, 10)));    // 第二次：几分钟后又刷了一次

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(8, 10)), mon.ClockOutTime);   // 取更晚的一次
    }

    [Fact]
    public void 夜班续接时间窗_下班时间后6小时内算昨天_之后不算()
    {
        var shift = NightShift();   // 周一夜班，周二 08:00 下班
        Assert.True(AttendanceService.IsWithinNightCarryOver(Mon, shift, Tue.ToDateTime(new TimeOnly(14, 0))));
        Assert.False(AttendanceService.IsWithinNightCarryOver(Mon, shift, Tue.ToDateTime(new TimeOnly(14, 1))));
    }

    [Fact]
    public void 一次卡都没打的很晚上班卡_宽限期要跟续接窗口一致_下班时间后6小时内仍算昨天()
    {
        // 2026-09-30 反馈：晚班员工缺了上班卡，后面的午间打卡和下班打卡都打不了。根因是这条判断以前卡在
        // "昨晚班次应下班时间"那一刻就截止，比"已经打了上班卡、只是没打下班卡"能续接的时间窗（下班时间后
        // 还有 6 小时宽限，见 IsWithinNightCarryOver）更短——一次卡都没打、拖到快下班/下班后一小会儿才想起来
        // 打第一次卡的人，会掉进这段空档，被拿"今晚"的班次误判成"打得太早"。
        var shift = NightShift();   // 周一夜班，周二 08:00 下班
        Assert.True(AttendanceService.IsVeryLateClockInForYesterdayShift(Mon, shift, null, Tue.ToDateTime(new TimeOnly(8, 0)), null));    // 正好下班时间：以前、现在都算
        Assert.True(AttendanceService.IsVeryLateClockInForYesterdayShift(Mon, shift, null, Tue.ToDateTime(new TimeOnly(14, 0)), null));   // 下班后6小时内：以前不算，现在算
        Assert.False(AttendanceService.IsVeryLateClockInForYesterdayShift(Mon, shift, null, Tue.ToDateTime(new TimeOnly(14, 1)), null));  // 超过6小时宽限：仍然不算
        Assert.False(AttendanceService.IsVeryLateClockInForYesterdayShift(Mon, shift, Mon.ToDateTime(new TimeOnly(20, 0)), Tue.ToDateTime(new TimeOnly(9, 0)), null));   // 已经打过上班卡的不归这条管
    }

    [Fact]
    public void 一次卡都没打的很晚上班卡_今天自己也排了班且已到打卡时刻_优先归今天不抢今天的记录()
    {
        // 2026-09-30 复核反馈：延长宽限期后，如果"今天"自己也排了班（哪怕是完全不同的班次，比如轮班），
        // 这次打卡已经到了今天班次自己可以打卡的时刻，就不该再被追认成昨天的——不然昨天请假/一次卡都没打时，
        // 今天正常的上班卡会被错误地记到昨天的（请假）记录上，一次弄乱两天。
        var yesterdayShift = NightShift();   // 周一夜班，周二 08:00 下班
        var todayShift      = DayShift();    // 周二白班，08:30 上班，最早 02:30 起可以打卡
        Assert.True(AttendanceService.IsVeryLateClockInForYesterdayShift(Mon, yesterdayShift, null, Tue.ToDateTime(new TimeOnly(8, 20)), null));                    // 今天没排班（传 null）：还是按旧口径算昨天
        Assert.False(AttendanceService.IsVeryLateClockInForYesterdayShift(Mon, yesterdayShift, null, Tue.ToDateTime(new TimeOnly(8, 20)), todayShift));            // 传了今天的班次：已经到了今天可以打卡的时刻，归今天
        Assert.True(AttendanceService.IsVeryLateClockInForYesterdayShift(Mon, yesterdayShift, null, Tue.ToDateTime(new TimeOnly(2, 29)), todayShift));             // 还没到今天可以打卡的时刻（02:30之前）：仍归昨天
    }

    [Fact]
    public async Task 昨晚夜班请假一次卡都没打_今天新排班次打卡_归今天不误写到昨天的请假记录()
    {
        var today     = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "跨天误判组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            // 昨晚夜班（应下班时间设成"现在往前1小时"，落在延长后的6小时宽限窗口里）；
            // 今天是完全不同的白班，应上班时间就设成"现在这一刻"，保证这次打卡已经到了今天可以打卡的时刻
            var yesterdayShift = new ShiftSchedule
            {
                ShiftName = "夜班", AttendanceGroupId = group.Id, WorkStartTime = new TimeOnly(20, 0), WorkEndTime = TimeOnly.FromDateTime(DateTime.Now.AddHours(-1)),
                IsCrossDay = true, LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 8, RestDaysOfWeek = ""
            };
            var todayShift = new ShiftSchedule
            {
                ShiftName = "白班", AttendanceGroupId = group.Id, WorkStartTime = TimeOnly.FromDateTime(DateTime.Now), WorkEndTime = new TimeOnly(18, 0),
                IsCrossDay = false, LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 8, RestDaysOfWeek = ""
            };
            db.ShiftSchedules.AddRange(yesterdayShift, todayShift);
            var user = new User { EmployeeNo = "N14", RealName = "跨天误判员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = yesterday, ShiftScheduleId = yesterdayShift.Id });
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = today, ShiftScheduleId = todayShift.Id });
            // 昨晚请了全天假：记录存在，但没有上班卡（跟审批通过时提前建空记录的行为一致）
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = user.Id, WorkDate = yesterday, AttendanceStatus = AttendanceStatus.OnLeave, LeaveHours = 8 });
            await db.SaveChangesAsync();
            uid = user.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var result = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockIn }, skipLocationCheck: true);
            Assert.True(result.Success, result.Message);
        }
        using var check = CreateContext();
        var todayRec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == today);
        Assert.NotNull(todayRec.ClockInTime);
        var yesterdayRec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == yesterday);
        Assert.Null(yesterdayRec.ClockInTime);                              // 昨天的请假记录没有被误写上班卡
        Assert.Equal(AttendanceStatus.OnLeave, yesterdayRec.AttendanceStatus);   // 请假状态也没被顺手改掉
    }

    [Fact]
    public async Task 夜班下班卡重复提交跨了分钟_第二次仍归昨天_不凭空多出今天的记录()
    {
        // 2026-09-30 复核反馈：开头的去重只挡"同一分钟"的重复提交。如果两次请求（网络重试/连点）
        // 刚好跨了一分钟，第二次会因为"昨天那条记录已经关闭"而查不到候选记录，退化成算作今天的打卡，
        // 把昨天的下班时间错误地写进今天的新记录——这里直接模拟"第一次请求已经在上一分钟把昨天关闭了"，
        // 验证第二次（这一分钟）请求仍然正确续到昨天，而不是凭空多出一条今天的记录。
        var today     = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        int uid;
        DateTime firstClockOut;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "跨分钟重复下班组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            var shift = new ShiftSchedule
            {
                ShiftName = "夜班", AttendanceGroupId = group.Id, WorkStartTime = new TimeOnly(20, 0), WorkEndTime = new TimeOnly(5, 30),
                IsCrossDay = true, LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 8, RestDaysOfWeek = ""
            };
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "N15", RealName = "跨分钟重复下班员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = yesterday, ShiftScheduleId = shift.Id });
            // 模拟"第一次下班打卡请求"在上一分钟已经把昨天这条记录关闭了
            firstClockOut = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, DateTime.Now.Minute, 0).AddMinutes(-1);
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = user.Id, WorkDate = yesterday, ClockInTime = yesterday.ToDateTime(new TimeOnly(20, 0)),
                ClockOutTime = firstClockOut, AttendanceStatus = AttendanceStatus.Normal
            });
            await db.SaveChangesAsync();
            uid = user.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var second = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockOut }, skipLocationCheck: true);
            Assert.True(second.Success, second.Message);
        }
        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == today));   // 不会凭空多出"今天"的记录
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == yesterday);
        Assert.True(rec.ClockOutTime >= firstClockOut);   // 下班时间按"取更晚"更新，仍在昨天这条记录上
    }

    [Fact]
    public async Task 白班过零点下班_重复提交跨了分钟_仍归昨天_不会误判成今天的上班卡()
    {
        // 2026-09-30 复核发现：§7.30 的兜底只认"昨天是跨天班次"。白班/没排班的人加班过零点下班
        // （下班卡本身已经打在零点之后），几分钟内重复提交（网络重试/连点）如果跨了分钟，之前会被误判成
        // 今天的上班卡，把今天真正的迟到分钟数、工时都算错——这里验证跨了分钟的重复提交仍然续到昨天。
        var today     = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        int uid;
        DateTime firstClockOut;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "白班过零点重复下班组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            var shift = new ShiftSchedule
            {
                ShiftName = "白班", AttendanceGroupId = group.Id, WorkStartTime = new TimeOnly(8, 30), WorkEndTime = new TimeOnly(17, 30),
                IsCrossDay = false, LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 8, RestDaysOfWeek = ""
            };
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "N16", RealName = "白班过零点重复下班员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = yesterday, ShiftScheduleId = shift.Id });
            // 模拟"第一次下班打卡请求"（加班过零点）在上一分钟已经把昨天关闭了——下班时间本身已经过了零点
            firstClockOut = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, DateTime.Now.Minute, 0).AddMinutes(-1);
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                UserId = user.Id, WorkDate = yesterday, ClockInTime = yesterday.ToDateTime(new TimeOnly(8, 25)),
                ClockOutTime = firstClockOut, AttendanceStatus = AttendanceStatus.Normal
            });
            await db.SaveChangesAsync();
            uid = user.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var second = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockOut }, skipLocationCheck: true);
            Assert.True(second.Success, second.Message);
        }
        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == today));   // 不会凭空多出"今天"的记录（以前白班不是跨天班次，这里接不住）
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == yesterday);
        Assert.True(rec.ClockOutTime >= firstClockOut);
    }

    [Fact]
    public async Task 白班加班过零点_考勤机连刷两次_不会盖掉第二天真正的迟到()
    {
        // 2026-09-30 复核发现的同一个问题，走考勤机（ZKDeviceSyncService）同步这条路径：
        // 加班到 00:40 下班，几分钟内连刷第二次（考勤机上很常见），第二次以前会被误判成今天的上班卡，
        // 导致第二天 09:10 真正到岗被当成"午间打卡"，迟到 40 分钟没记上、工时也多算了。
        var uid = SeedDayDeviceWorld();
        await SyncDayAsync(Tue.ToDateTime(new TimeOnly(0, 40)), Tue.ToDateTime(new TimeOnly(0, 45)), Tue.ToDateTime(new TimeOnly(9, 10)));

        using var check = CreateContext();
        var mon = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(0, 45)), mon.ClockOutTime);   // 两次重复刷卡取更晚的一次，仍在周一
        var tue = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(9, 10)), tue.ClockInTime);    // 09:10 才是周二真正的上班卡
        Assert.Equal(40, tue.LateMinutes);                                     // 迟到40分钟没有被盖掉
    }

    [Fact]
    public async Task 晚班一次卡都没打_拖到下班时间后几小时才打第一次卡_仍记到昨天那班_不再被误判成打得太早()
    {
        var today     = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "晚班缺卡组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            // 班次应下班时间设成"现在往前数一点点"，保证不管测试什么时候跑，"现在"都落在
            // "下班时间" ~ "下班时间+6小时宽限" 这个窗口内（以前会被误判的那段空档）
            var endTime = TimeOnly.FromDateTime(DateTime.Now.AddHours(-1));
            var shift = new ShiftSchedule
            {
                ShiftName = "晚班", AttendanceGroupId = group.Id, WorkStartTime = new TimeOnly(20, 30), WorkEndTime = endTime,
                IsCrossDay = true, LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 8, RestDaysOfWeek = ""
            };
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "N11", RealName = "晚班缺卡员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            // 今天故意不排班：这条测试要验证的是"昨天完全没打卡能不能被追认"，跟"今天自己是不是也排了班"
            // 无关——如果今天也排了同一班次，测试跑到下午（今天班次自己的可打卡时刻已到）时会被 §7.29
            // 的"今天已到可打卡时刻就优先归今天"抢先命中，变成看运行时刻而定（2026-09-30 发现）。
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = yesterday, ShiftScheduleId = shift.Id });
            await db.SaveChangesAsync();
            uid = user.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var result = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockIn }, skipLocationCheck: true);
            Assert.True(result.Success, result.Message);   // 以前这里会被拒绝，提示"最早XX点起可以打上班卡"
        }
        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == today));   // 不会凭空多出"今天"的记录
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == yesterday);
        Assert.NotNull(rec.ClockInTime);
    }

    [Fact]
    public async Task 晚班缺上班卡_补卡申请被驳回后_仍能正常打下班卡_不会凭空多出第二天的记录()
    {
        // 用户要求（2026-09-30）：夜班员工忘记打上班卡，即使补卡申请没有通过，也要能正常打下班卡，
        // 而且不能跟"第二天"混在一起——补卡申请被驳回（没有回写考勤，考勤记录该缺照样缺）之后，
        // 靠现场打卡也能把昨晚这班完整收尾（上下班卡都有），过程中不会凭空多出一条"今天"的记录。
        // "已经打过上班卡的记录不会再被当成一次新的很晚上班卡"这条边界，由
        // 一次卡都没打的很晚上班卡_宽限期要跟续接窗口一致_下班时间后6小时内仍算昨天() 这条纯函数测试单独覆盖。
        var today     = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "晚班缺卡驳回组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            // 跟上一条测试同样的手法：应下班时间设成"现在往前 1 小时"，保证不管测试什么时候跑，
            // "现在"都落在下班时间之后的 6 小时宽限窗口内
            var shift = new ShiftSchedule
            {
                ShiftName = "晚班", AttendanceGroupId = group.Id, WorkStartTime = new TimeOnly(20, 30), WorkEndTime = TimeOnly.FromDateTime(DateTime.Now.AddHours(-1)),
                IsCrossDay = true, LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 8, RestDaysOfWeek = ""
            };
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "N12", RealName = "晚班缺卡驳回员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            // 今天故意不排班，理由同上一条测试：避免 §7.29 的"今天已到可打卡时刻"判断在下午跑测试时抢先命中
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = yesterday, ShiftScheduleId = shift.Id });
            // 昨晚忘打上班卡，员工提交了补卡申请，但审批人驳回了——驳回不回写考勤，
            // 考勤记录本身应该继续保持"完全没有"这条记录的状态
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "BK-REJ-1", ApplicantUserId = user.Id, ApprovalType = ApprovalType.PunchReplenishment,
                ApprovalStatus = ApprovalStatus.Rejected, PunchDate = yesterday, PunchType = PunchType.ClockIn,
                PunchTime = new TimeOnly(20, 30), Reason = "忘打卡", SubmittedAt = DateTime.Now, UpdatedAt = DateTime.Now
            });
            await db.SaveChangesAsync();
            uid = user.Id;
        }
        using (var db = CreateContext())
            Assert.False(await db.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == yesterday));   // 驳回没有留下任何考勤记录

        // 现场补打：先是"很晚的上班卡"（追认昨晚），紧接着真正的下班卡——两次都是独立的请求/DbContext
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var clockIn = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockIn }, skipLocationCheck: true);
            Assert.True(clockIn.Success, clockIn.Message);
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var clockOut = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockOut }, skipLocationCheck: true);
            Assert.True(clockOut.Success, clockOut.Message);
        }

        using (var check = CreateContext())
        {
            Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == today));   // 不会凭空多出"今天"的记录
            var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == yesterday);
            Assert.NotNull(rec.ClockInTime);
            Assert.NotNull(rec.ClockOutTime);
            // 用 >= 而不是 >：两次打卡在测试里几乎同时发生，可能落在同一分钟（打卡时间精确到分钟），
            // 这里只验证没有再出现"下班时间倒挂在上班时间之前"那种原始 bug，不要求严格晚于
            Assert.True(rec.ClockOutTime >= rec.ClockInTime);   // 昨晚这班完整收尾（上下班卡都有，且顺序没有倒挂）
        }
    }

    // ── ⑫ 夜班上班卡合理性：下班后重复刷、打得太早（2026-09-28 线上 13 条夜班记录被弄乱）───────

    private async Task CloseMondayNightShiftAsync(DateTime clockOut)
    {
        using var db = CreateContext();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.WorkDate == Mon);
        rec.ClockOutTime = clockOut;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task 夜班刚打完下班卡_52秒后又点了一次_被拒绝_不生成新的上班卡()
    {
        var (uid, _) = SeedNightWorld();                                    // 周一 20:00 上班，周二 08:00 下班（跨天班次）
        await CloseMondayNightShiftAsync(Tue.ToDateTime(new TimeOnly(8, 40)));

        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var msg = await svc.GetClockInRejectionAsync(uid, Tue.ToDateTime(new TimeOnly(8, 40, 53)));
        Assert.NotNull(msg);
        Assert.Contains("刚刚", msg);
        Assert.Contains("08:40", msg);
    }

    [Theory]
    [InlineData(9, 30, true)]     // 下班后 50 分钟：不算"重复刷"，但离周二 20:00 上班还早 → 太早
    [InlineData(13, 59, true)]    // 上班时间前 6 小时零 1 分钟：太早
    [InlineData(14, 0, false)]    // 正好前 6 小时：可以
    [InlineData(19, 51, false)]   // 晚上来上班：可以（肖文城 9/27 那次）
    public async Task 跨天班次的上班卡_离应上班时间前6小时以外的被拒绝(int h, int m, bool rejected)
    {
        var (uid, _) = SeedNightWorld();
        await CloseMondayNightShiftAsync(Tue.ToDateTime(new TimeOnly(8, 0)));

        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        var msg = await svc.GetClockInRejectionAsync(uid, Tue.ToDateTime(new TimeOnly(h, m)));
        Assert.Equal(rejected, msg is not null);
        if (rejected && (h, m) != (9, 30)) Assert.Contains("14:00", msg);   // 提示里写明最早几点可以打
    }

    [Fact]
    public async Task 白班的上班卡_不受这条限制()
    {
        var (uid, _) = SeedWeekWorld();   // 周五~周一白班 08:30 上班，不是跨天班次
        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        Assert.Null(await svc.GetClockInRejectionAsync(uid, Mon.ToDateTime(new TimeOnly(5, 0))));   // 再早也不拦
    }

    [Fact]
    public async Task 今天已经有上班卡_不再拦截()
    {
        var (uid, _) = SeedNightWorld();
        using (var db = CreateContext())
        {
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = uid, WorkDate = Tue, ClockInTime = Tue.ToDateTime(new TimeOnly(19, 40)) });
            db.SaveChanges();
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        Assert.Null(await svc.GetClockInRejectionAsync(uid, Tue.ToDateTime(new TimeOnly(9, 0))));
    }

    [Fact]
    public async Task 考勤机同步_跨天班次的第一次打卡离上班时间太早_不当上班卡_也不建记录()
    {
        var (uid, _) = SeedNightWorld();
        await CloseMondayNightShiftAsync(Tue.ToDateTime(new TimeOnly(8, 0)));
        await SyncAsync(Tue.ToDateTime(new TimeOnly(12, 0)));     // 周二中午（离 20:00 上班还有 8 小时）

        using (var check = CreateContext())
            Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));   // 以前这里会建一条 12:00 的上班卡

        await SyncAsync(Tue.ToDateTime(new TimeOnly(19, 40)));    // 晚上来上班：正常记上班卡
        using var check2 = CreateContext();
        var tue = await check2.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Tue);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(19, 40)), tue.ClockInTime);
    }

    // ── ⑯ 2026-09-29 用户反馈：夜班一次卡都没打，凌晨才想起来打上班卡，被"打得太早"误拦 ──────
    // 2026-09-30 再次反馈：这条判断的宽限期以前卡在"昨晚班次应下班时间"那一刻，比"已经打了上班卡、
    // 只是没打下班卡"能续接的时间窗（下班时间后还有 6 小时宽限，见 IsWithinNightCarryOver）更短，两者
    // 现在统一成同一个宽限期——下面的边界值也跟着从"下班时间那一刻"挪到"下班时间 + 6 小时"。

    // ★ NightShift() 是 20:00~08:00 严格对称的 12 小时班次，"昨天下班+6小时宽限"（14:00）跟"今天上班-6小时"
    // （20:00-6=14:00）这两个本来各管各的边界，在这套时间参数下刚好重合到同一分钟——超过 14:00 之后，
    // 既不再算"昨晚很晚的上班卡"，也同时不再算"离今晚班次太早"，所以这个 Theory 只验证到边界为止；
    // "超过 6 小时宽限确实不再追认成昨天"这一半单独用下面的纯函数测试验证（不受"今天班次"这个变量干扰）。
    [Theory]
    [InlineData(0, 29, false)]     // 昨晚 20:00 上班，现在凌晨 00:29：还在昨晚班次结束（08:00）之前，应该放行
    [InlineData(7, 59, false)]     // 还差 1 分钟到昨晚班次的下班时间：仍放行
    [InlineData(8, 1, false)]      // 刚过昨晚班次下班时间：仍在 6 小时宽限期内，仍然放行（2026-09-30 修复前这里会被拒绝）
    [InlineData(14, 0, false)]     // 下班时间后整 6 小时：宽限期最后一刻，仍放行
    public async Task 夜班一次卡都没打_凌晨才打卡_只要没超过昨晚班次下班时间加6小时宽限就不算太早(int h, int m, bool stillRejected)
    {
        var (uid, _) = SeedNightWorld();
        using (var db = CreateContext())
        {
            // 把 SeedNightWorld 帮我们建好的"周一 20:00 已打上班卡"抹掉，改成"周一一次卡都没打"（更贴近真实反馈的场景）
            var mon = await db.AttendanceRecords.SingleAsync(r => r.WorkDate == Mon);
            db.AttendanceRecords.Remove(mon);
            await db.SaveChangesAsync();
        }
        using var db2 = CreateContext();
        var svc = new AttendanceService(db2, AppOptions, NullLogger<AttendanceService>.Instance);
        var msg = await svc.GetClockInRejectionAsync(uid, Tue.ToDateTime(new TimeOnly(h, m)));
        if (stillRejected)
        {
            Assert.NotNull(msg);
            Assert.Contains("最早", msg);
        }
        else
        {
            Assert.Null(msg);
        }
    }

    [Fact]
    public async Task 夜班下班卡被重复提交_第二次不会把昨天的下班时间错记到今天()
    {
        // 2026-09-30 从李杰的真实记录里发现的事故：下班打卡的请求被提交了两次（网络重试/连点）。
        // 第一次正确续到昨天那条跨天夜班记录并关闭它；如果去重只挡住"写打卡流水"这一步、不挡住
        // "这次打卡归到哪一天"的判断，第二次请求就会因为"昨天已经关闭"而判断不成立，退化成算作
        // 今天的打卡，把昨天的下班时间错误地写进一条凭空多出来的"今天"记录里。
        var today     = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "夜班重复打卡组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            var shift = new ShiftSchedule
            {
                ShiftName = "夜班", AttendanceGroupId = group.Id, WorkStartTime = new TimeOnly(20, 0), WorkEndTime = new TimeOnly(23, 59),
                IsCrossDay = true, LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 3, RestDaysOfWeek = ""
            };
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "N10", RealName = "夜班重复打卡员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            foreach (var d in new[] { yesterday, today })
                db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = shift.Id });
            // 昨晚已经打了上班卡，还没打下班卡
            db.AttendanceRecords.Add(new AttendanceRecord { UserId = user.Id, WorkDate = yesterday, ClockInTime = yesterday.ToDateTime(new TimeOnly(20, 0)), AttendanceStatus = AttendanceStatus.Normal });
            db.SaveChanges();
            uid = user.Id;
        }

        // 同一次下班打卡，模拟被提交了两次（各用一个新的 DbContext，跟两次独立的 HTTP 请求一致）
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var first = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockOut }, skipLocationCheck: true);
            Assert.True(first.Success, first.Message);
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var second = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockOut }, skipLocationCheck: true);
            Assert.True(second.Success, second.Message);
        }

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == today));   // 不会凭空多出"今天"的记录
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == yesterday);
        Assert.NotNull(rec.ClockOutTime);
        Assert.True(rec.ClockOutTime > rec.ClockInTime);
        Assert.Equal(1, await check.AttendancePunches.CountAsync(p => p.UserId == uid && p.PunchType == PunchType.ClockOut));
    }

    [Fact]
    public async Task 夜班一次卡都没打_凌晨打卡成功后_记到昨天那班_算很晚的迟到_不会凭空多出今天的记录()
    {
        // GetClockInRejectionAsync 只能验证"拦不拦"，实际落库走的是 PunchAsync（内部用 DateTime.Now，
        // 没法像别的测试那样注入固定的 Mon/Tue），所以这条改用真实的"今天/昨天"，并把班次的下班时间
        // 设得很晚（23:59），保证不管测试什么时候跑，"现在"都落在"昨晚班次结束之前"这个窗口内。
        // ★ 今天故意不排班（只给昨天排）：这条测试要验证的是"完全没打卡的昨天能不能被追认"，
        // 跟"今天自己是不是也排了班"无关——如果今天也排了同一个班次，一旦测试跑到下午（今天班次
        // 自己的可打卡时刻已到），§7.29 的"今天已到可打卡时刻就优先归今天"这条判断会抢先命中，
        // 导致这次打卡被错误地记成"今天"的新记录，测试变得看运行时刻而定（2026-09-30 发现）。
        var today     = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "夜班一次没打组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();
            var shift = new ShiftSchedule
            {
                ShiftName = "夜班", AttendanceGroupId = group.Id, WorkStartTime = new TimeOnly(20, 0), WorkEndTime = new TimeOnly(23, 59),
                IsCrossDay = true, LateToleranceMinutes = 5, EarlyLeaveToleranceMinutes = 5, StandardWorkHours = 3, RestDaysOfWeek = ""
            };
            db.ShiftSchedules.Add(shift);
            var user = new User { EmployeeNo = "N9", RealName = "夜班没打卡员工", PasswordHash = "x", IsActive = true, AttendanceGroupId = group.Id, HireDate = new DateOnly(2026, 1, 1) };
            db.Users.Add(user);
            db.SaveChanges();
            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = yesterday, ShiftScheduleId = shift.Id });
            db.SaveChanges();
            uid = user.Id;
        }
        using (var db = CreateContext())
        {
            var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var result = await svc.PunchAsync(uid, new PunchRequestDto { PunchType = PunchType.ClockIn }, skipLocationCheck: true);
            Assert.True(result.Success, result.Message);
        }
        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == today));   // 没有凭空多出"今天"的记录
        var rec = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == yesterday);
        Assert.NotNull(rec.ClockInTime);
        Assert.Equal(AttendanceStatus.Late, rec.AttendanceStatus);
        Assert.True(rec.LateMinutes > 0);
    }

    [Fact]
    public async Task 设备同步_夜班一次卡都没打_凌晨的脸识别记成昨晚很晚的上班卡()
    {
        var (uid, _) = SeedNightWorld();
        using (var db = CreateContext())
        {
            var mon = await db.AttendanceRecords.SingleAsync(r => r.WorkDate == Mon);
            db.AttendanceRecords.Remove(mon);   // 周一一次卡都没打
            await db.SaveChangesAsync();
        }
        await SyncAsync(Tue.ToDateTime(new TimeOnly(0, 29)));

        using var check = CreateContext();
        Assert.False(await check.AttendanceRecords.AnyAsync(r => r.UserId == uid && r.WorkDate == Tue));
        var mon2 = await check.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == Mon);
        Assert.Equal(Tue.ToDateTime(new TimeOnly(0, 29)), mon2.ClockInTime);
        Assert.Equal(AttendanceStatus.Late, mon2.AttendanceStatus);
    }
}
