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
/// ● 每月 1 日 00:02：生成上一个月的考勤汇总；
/// ● （可选）按配置间隔从钉钉拉取打卡（默认关，由 DingTalk.EnableScheduledSync 打开）。
/// 用「上次执行日期」做记号，保证同一时间窗内只执行一次。
/// </summary>
public class AttendanceBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<DingTalkOptions> dingTalkOptions,
    ILogger<AttendanceBackgroundService> logger)
    : BackgroundService
{
    // 记录三类任务“上次执行的时间”，避免在同一分钟窗口里重复跑
    private DateTime _lastAbsentDate    = DateTime.MinValue;
    private DateTime _lastSummaryDate   = DateTime.MinValue;
    private DateTime _lastDingTalkSync  = DateTime.MinValue;

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

                // 每月 1 日 00:02 且这个月还没生成过 → 生成上个月汇总
                if (now.Day == 1 && now.Hour == 0 && now.Minute >= 2 && _lastSummaryDate.Date < now.Date)
                {
                    _lastSummaryDate = now;
                    var prev = now.AddMonths(-1);   // 上个月
                    await GenerateSummaryAsync(prev.Year, prev.Month);
                }

                // 钉钉定时同步（默认关闭；开启后每隔 N 分钟拉一次）
                var dt = dingTalkOptions.Value;
                if (dt.EnableScheduledSync &&
                    (now - _lastDingTalkSync).TotalMinutes >= Math.Max(1, dt.ScheduledSyncIntervalMinutes))
                {
                    _lastDingTalkSync = now;
                    await SyncDingTalkAsync(dt);
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

            // 已是「请假/休假/出差」的记录（如钉钉请假同步写入、出差审批回写）不要覆盖成旷工
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

    /// <summary>按配置回溯天数，从钉钉拉取打卡结果并落库。</summary>
    private async Task SyncDingTalkAsync(DingTalkOptions opt)
    {
        using var scope = scopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IDingTalkSyncService>();
        var to   = DateTime.Now;
        var from = DateTime.Today.AddDays(-Math.Max(0, opt.ScheduledSyncLookbackDays));
        var result = await sync.SyncAsync(from, to);
        logger.LogInformation("钉钉定时同步：{Message}", result.Message);
    }
}
