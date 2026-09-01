using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.BackgroundServices;

// 「后台定时任务」= 程序在后台自己跑的一个循环，不需要人去点，到点自动干活。

/// <summary>
/// 考勤后台定时任务（每分钟检查一次）：
/// ● 每天 23:58：把当天没打卡的在职员工标记为旷工/未打卡；
/// ● 每月 1 日 00:10：生成上一个月的考勤汇总（比月初留几分钟缓冲，给设备重传/网络延迟一点时间，
///   免得月末最后几分钟的打卡因为还没到账就被漏算进汇总）；
/// ● 每天 03:00：清理考勤机相关的过期数据（已确认的命令记录、过期的考勤照片）。
/// 用「上次执行日期」做记号，保证同一时间窗内只执行一次。
/// </summary>
public class AttendanceBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AttendanceBackgroundService> logger)
    : BackgroundService
{
    // 记录三类任务“上次执行的时间”，避免在同一分钟窗口里重复跑
    private DateTime _lastAbsentDate    = DateTime.MinValue;
    private DateTime _lastSummaryDate   = DateTime.MinValue;
    private DateTime _lastCleanupDate   = DateTime.MinValue;

    // 程序启动后这个方法一直在后台循环运行，直到程序关闭
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)   // 没收到“停止”信号就一直循环
        {
            try
            {
                var now = DateTime.Now;

                // 到 23:58 且今天还没标记过 → 标记旷工
                if (now.Hour == 23 && now.Minute >= 58 && _lastAbsentDate.Date < now.Date)
                {
                    _lastAbsentDate = now;
                    await MarkAbsentAsync();
                }

                // 每月 1 日 00:10 且这个月还没生成过 → 生成上个月汇总
                if (now.Day == 1 && now.Hour == 0 && now.Minute >= 10 && _lastSummaryDate.Date < now.Date)
                {
                    _lastSummaryDate = now;
                    var prev = now.AddMonths(-1);   // 上个月
                    await GenerateSummaryAsync(prev.Year, prev.Month);
                }

                // 每天 03:00 且今天还没清理过 → 清理考勤机过期数据
                if (now.Hour == 3 && _lastCleanupDate.Date < now.Date)
                {
                    _lastCleanupDate = now;
                    await CleanupZKDeviceDataAsync();
                }
            }
            catch (Exception ex)
            {
                // 后台任务出错不能让循环崩掉，记下日志继续跑
                logger.LogError(ex, "考勤后台任务异常");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);   // 歇 1 分钟再检查
        }
    }

    /// <summary>
    /// 扫描当天所有在职员工：没记录/没打上班卡 → 旷工；打了上班卡但没打下班卡 → 未打卡。
    /// 节假日（以及非补班日的周末）跳过；对旷工/缺卡发提醒通知。
    /// </summary>
    private async Task MarkAbsentAsync()
    {
        // 后台任务里要自己开一个“作用域”来拿数据库（不能直接用构造函数注入的，生命周期不同）
        using var scope = scopeFactory.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var users = await db.Users.Where(u => u.IsActive).ToListAsync();

        // 一次性把“今天的假期”“今天已有的考勤记录”“今天的排班”查出来放内存，循环里直接用，避免逐人查库（N+1）
        var todayHolidays = await db.Holidays.Where(h => h.HolidayDate == today).ToListAsync();
        var recordByUser  = (await db.AttendanceRecords.Where(r => r.WorkDate == today).ToListAsync())
            .GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.First());
        // 今天排了班的人，连同班次一起取出来——用来判断"这个班次自己配置的每周休息日"是不是命中了今天
        var todayAssignmentByUser = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => a.WorkDate == today)
                .ToListAsync())
            .GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.First());

        // 这个考勤组今天是不是休息日（法定/公司休息，但调班补班日不算休息）
        // 说明：这里判断“是不是节假日”的逻辑，和 AttendanceService.IsHolidayAsync 看起来很像，
        // 但故意没有直接复用它——因为 IsHolidayAsync 每次调用都会查一次数据库，
        // 这里是在“循环里对每个员工都要判断一次”，如果每次都去查数据库，
        // 几百个员工就要查几百次（也就是前面注释说的 N+1 问题）。
        // 所以这里改成先把"今天的假期"整批查一次（todayHolidays），后面循环里直接从内存里判断，不再查库。
        bool IsRestDay(int? groupId) => todayHolidays.Any(h =>
            h.HolidayType != HolidayType.CompensatoryWorkDay &&
            (h.AttendanceGroupId == null || h.AttendanceGroupId == groupId));
        // 这个考勤组今天是不是调班补班日（哪怕是周末也要上班）
        bool IsCompensatoryWorkday(int? groupId) => todayHolidays.Any(h =>
            h.HolidayType == HolidayType.CompensatoryWorkDay &&
            (h.AttendanceGroupId == null || h.AttendanceGroupId == groupId));

        var isWeekend = today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        int marked = 0;

        foreach (var user in users)
        {
            if (IsRestDay(user.AttendanceGroupId)) continue;                          // 休息日不处理
            var isCompDay = IsCompensatoryWorkday(user.AttendanceGroupId);
            if (isWeekend && !isCompDay) continue;                                    // 普通周末不处理

            // 补班日之外，如果今天排的班次自己配了"每周休息日"、命中了今天，也当休息处理
            // （比如三班倒可能休二、三，不是标准的周六周日；但补班日是公司统一要求上班，优先级更高，不受这个影响）
            if (!isCompDay &&
                todayAssignmentByUser.TryGetValue(user.Id, out var todayAssignment) &&
                todayAssignment.ShiftSchedule.IsRestDay(today.DayOfWeek))
                continue;

            recordByUser.TryGetValue(user.Id, out var record);   // 取这个人今天的考勤记录（可能没有）

            // 已是「请假/休假/出差」的记录（如请假审批、出差审批回写）不要覆盖成旷工
            if (record is not null && record.AttendanceStatus is AttendanceStatus.OnLeave or AttendanceStatus.Holiday or AttendanceStatus.BusinessTrip)
                continue;

            if (record is null)
            {
                // 完全没记录 → 新建一条“旷工”，并发提醒
                db.AttendanceRecords.Add(new AttendanceRecord
                {
                    UserId           = user.Id,
                    WorkDate         = today,
                    AttendanceStatus = AttendanceStatus.Absent,
                    UpdatedAt        = DateTime.Now
                });
                db.Notifications.Add(new Notification
                {
                    UserId           = user.Id,
                    Title            = "今日旷工提醒",
                    Content          = $"您今日（{today:MM/dd}）未打卡，已被标记为旷工，如有异议请提交补卡申请",
                    NotificationType = "PunchReminder",
                    CreatedAt        = DateTime.Now
                });
                marked++;
            }
            else if (record.ClockInTime is null)
            {
                // 有记录但没打上班卡 → 旷工
                record.AttendanceStatus = AttendanceStatus.Absent;
                record.UpdatedAt        = DateTime.Now;
                marked++;
            }
            else if (record.ClockOutTime is null)
            {
                // 夜班（跨天班次）今天刚打上班卡，要到明天凌晨才下班——现在人还在班上，不是"没打卡"，
                // 等明天下班打卡时这条记录会正常续上；这里提前标记会导致刚上班没多久的夜班员工被误报，
                // 而且下班打卡时只有"早退"才会纠正状态（见 AttendanceService.PunchAsync），正常下班这个误标记不会被清掉
                if (todayAssignmentByUser.TryGetValue(user.Id, out var crossDayAssign) && crossDayAssign.ShiftSchedule.IsCrossDay)
                    continue;

                // 打了上班卡但没打下班卡 → 未打卡，并发提醒
                record.AttendanceStatus = AttendanceStatus.NotPunched;
                record.UpdatedAt        = DateTime.Now;
                db.Notifications.Add(new Notification
                {
                    UserId           = user.Id,
                    Title            = "下班未打卡提醒",
                    Content          = $"您今日（{today:MM/dd}）未打下班卡，如有异议请提交补卡申请",
                    NotificationType = "PunchReminder",
                    CreatedAt        = DateTime.Now
                });
            }
        }

        // 昨天是不是有夜班（跨天班次）打了上班卡、一直没打下班卡的记录——昨天这个时间点检查时，
        // 因为"人可能还在上班、要到今天凌晨才下班"特意跳过了（见上面 IsCrossDay 那个 continue）。
        // 现在已经过了整整一天，如果还是没有下班卡，说明是真的漏打了（忘记打卡/离职/设备故障），
        // 需要在这里补上标记——不然这条记录会永远停在"已上班未下班"，旷工/未打卡看板永远看不到、
        // 也永远收不到提醒（因为后续每天的检查只看"今天"的记录，不会再回头看这条）。
        var yesterday        = today.AddDays(-1);
        var activeUserIds    = users.Select(u => u.Id).ToHashSet();   // 复用上面已查好的"当前在职员工"名单
        var yesterdayOpenRecords = (await db.AttendanceRecords
            .Where(r => r.WorkDate == yesterday && r.ClockInTime != null && r.ClockOutTime == null
                     && r.AttendanceStatus != AttendanceStatus.NotPunched
                     && r.AttendanceStatus != AttendanceStatus.OnLeave
                     && r.AttendanceStatus != AttendanceStatus.Holiday
                     && r.AttendanceStatus != AttendanceStatus.BusinessTrip)
            .ToListAsync())
            .Where(r => activeUserIds.Contains(r.UserId))   // 已离职/停用的人不再标记、不再发提醒
            .ToList();
        if (yesterdayOpenRecords.Count > 0)
        {
            var openUserIds = yesterdayOpenRecords.Select(r => r.UserId).ToList();
            var yesterdayAssignByUser = (await db.ShiftAssignments
                    .Include(a => a.ShiftSchedule)
                    .Where(a => a.WorkDate == yesterday && openUserIds.Contains(a.UserId))
                    .ToListAsync())
                .GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.First());

            foreach (var record in yesterdayOpenRecords)
            {
                // 只处理"昨天排的确实是跨天班次"这种情况——普通白班漏打下班卡当天就已经处理过了，
                // 不会走到这里；这里只是给夜班这一类"故意延后一天再判定"的情况兜底。
                if (!yesterdayAssignByUser.TryGetValue(record.UserId, out var assign) || !assign.ShiftSchedule.IsCrossDay)
                    continue;

                record.AttendanceStatus = AttendanceStatus.NotPunched;
                record.UpdatedAt        = DateTime.Now;
                db.Notifications.Add(new Notification
                {
                    UserId           = record.UserId,
                    Title            = "下班未打卡提醒",
                    Content          = $"您 {yesterday:MM/dd} 的夜班一直未打下班卡，如有异议请提交补卡申请",
                    NotificationType = "PunchReminder",
                    CreatedAt        = DateTime.Now
                });
                marked++;
            }
        }

        await db.SaveChangesAsync();
        logger.LogInformation("旷工标记完成，日期：{Date}，标记 {Count} 人", today, marked);
    }

    /// <summary>调用考勤服务生成某月汇总。</summary>
    private async Task GenerateSummaryAsync(int year, int month)
    {
        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAttendanceService>();
        await svc.GenerateMonthlySummaryAsync(year, month);
        logger.LogInformation("月度汇总生成完成：{Year}/{Month}", year, month);
    }

    /// <summary>
    /// 清理考勤机 + 远程打卡相关的过期数据，避免相关表/目录一直只增不删：
    /// ① 已经收到设备确认（Confirmed=true）超过 RetentionDays 天的考勤机命令记录——确认过的命令不会再被
    ///    重新下发，留着只是历史记录，没有查询价值；
    /// ② 超过 RetentionDays 天的考勤照片（ATTPHOTO）和远程打卡现场照片——目前都是只写不读的留痕数据，
    ///    放着只会一直占磁盘；
    /// ③ 超过 RetentionDays 天的人脸识别尝试记录（FaceVerifyAttempt）——只在限流查询里用到最近几分钟内的，
    ///    更早的没有查询价值。
    /// 保留天数和 Serilog 日志一致（30 天），不给运维增加新的心智负担。
    /// </summary>
    private async Task CleanupZKDeviceDataAsync()
    {
        const int retentionDays = 30;
        var cutoff = DateTime.Now.AddDays(-retentionDays);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();

        var deletedCommands = await db.ZKDeviceCommands
            .Where(c => c.Confirmed && c.ConfirmedAt != null && c.ConfirmedAt < cutoff)
            .ExecuteDeleteAsync();

        var deletedAttempts = await db.FaceVerifyAttempts
            .Where(a => a.CreatedAt < cutoff)
            .ExecuteDeleteAsync();

        var deletedZkPhotoDirs   = CleanupOldDateDirs(scope, "zkdevice", cutoff);
        var deletedFacePhotoDirs = CleanupOldDateDirs(scope, Path.Combine("faces", "attempts"), cutoff);

        logger.LogInformation(
            "考勤机/远程打卡数据清理完成：删除已确认命令 {CmdCount} 条，删除人脸尝试记录 {AttemptCount} 条，" +
            "删除考勤照片目录 {ZkDirCount} 个，删除远程打卡照片目录 {FaceDirCount} 个",
            deletedCommands, deletedAttempts, deletedZkPhotoDirs, deletedFacePhotoDirs);
    }

    /// <summary>删掉 wwwroot/{UploadPath}/{subPath} 下文件夹名能解析成日期、且早于 cutoff 的整个目录
    /// （目录名格式是 yyyyMMdd，按文件夹名判断即可，不用挨个读文件的创建时间）。</summary>
    private static int CleanupOldDateDirs(IServiceScope scope, string subPath, DateTime cutoff)
    {
        var env        = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var appOptions = scope.ServiceProvider.GetRequiredService<IOptions<AppSettingsOptions>>().Value;

        var uploadPath = appOptions.UploadPath.Trim('/', '\\');
        var webRoot    = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var root       = Path.Combine(webRoot, uploadPath, subPath);
        if (!Directory.Exists(root)) return 0;

        var deleted = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (DateTime.TryParseExact(name, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dirDate)
                && dirDate < cutoff.Date)
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
        }
        return deleted;
    }
}
