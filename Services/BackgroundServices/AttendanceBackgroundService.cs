using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.BackgroundServices;

// 「后台定时任务」= 程序在后台自己跑的一个循环，不需要人去点，到点自动干活。

/// <summary>
/// 考勤后台定时任务（每分钟检查一次）：
/// ● 每天 23:55-23:59：把当天没打卡的在职员工标记为旷工/未打卡；
/// ● 每月 1-3 日：生成上一个月的考勤汇总（1 号 00:10 之后是首选时间点，留几分钟缓冲给设备重传/网络延迟；
///   如果 1 号那次因为异常/重启被错过，2、3 号任意时间都会自动补跑一次，不用等人工点"重新生成"）；
/// ● 每天 03:00：清理考勤机相关的过期数据（已确认的命令记录、过期的考勤照片）、7 天前已读的审批提醒通知；
/// ● 每隔 4 小时：给挂了 4 小时以上还没处理的待审批申请，往当前该处理的审批人发一条汇总提醒通知（每人每轮一条）
///   （管理员/文员登录页面提交后就容易忘，光靠提交那一刻发的一条通知很容易被日常消息淹没）。
/// 用「上次执行时间」做记号，保证同一时间窗内只执行一次；这个记号只在对应任务真正跑成功之后才会更新，
/// 半途异常不会被误记成"已完成"，下一分钟还会重试。
/// </summary>
public class AttendanceBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AttendanceBackgroundService> logger)
    : BackgroundService
{
    // 记录几类任务"上次执行的时间"，避免在同一时间窗内重复跑
    private DateTime _lastAbsentDate            = DateTime.MinValue;
    private DateTime _lastSummaryDate           = DateTime.MinValue;
    private DateTime _lastCleanupDate           = DateTime.MinValue;
    private DateTime _lastApprovalReminderAt    = DateTime.MinValue;

    // 程序启动后这个方法一直在后台循环运行，直到程序关闭
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)   // 没收到“停止”信号就一直循环
        {
            try
            {
                var now = DateTime.Now;

                // 到 23:55-23:59 且今天还没标记过 → 标记旷工。窗口从原来的 23:58-23:59（2 分钟）
                // 放宽到 5 分钟，给一次任务内部瞬时失败（比如数据库短暂抖动）留出重试机会——
                // 下面 _lastAbsentDate 的更新挪到执行成功之后，异常会被外层 catch 记录但不会
                // 把这天误标记成"已处理"，同一晚窗口内下一分钟还会自动重试。
                if (now.Hour == 23 && now.Minute >= 55 && _lastAbsentDate.Date < now.Date)
                {
                    await MarkAbsentAsync(DateOnly.FromDateTime(now.Date));
                    _lastAbsentDate = now;
                }
                // 补跑：如果 23:55-23:59 这 5 分钟整窗口都没标记成功（数据库宕机超过 5 分钟、应用在
                // 这段时间反复重启等），过了午夜 now.Hour == 23 就再也不成立，原来没有任何补跑路径，
                // 当天的旷工/未打卡会被永久跳过。这里跟月度汇总的 2、3 号补跑是同一个思路：次日凌晨
                // 0-3 点这段时间，只要发现"昨天"还没标记过，就把从上次标记成功的次日起、到昨天为止
                // 逐天补标一遍（MarkAbsentAsync 对同一天重复调用是幂等的：旷工状态只是覆盖，不会
                // 重复插入记录；提醒通知只在"当天完全没有记录"这个分支发一次，重复调用不会再命中，
                // 不会重复打扰员工）。_lastAbsentDate 是 MinValue（比如刚上线还从没标记过）时，
                // 只补昨天一天，不会一路补到系统最早上线那天（2026-09-21 新增）。
                else if (now.Hour is >= 0 and < 3 && _lastAbsentDate.Date < now.Date.AddDays(-1))
                {
                    var yesterday = now.Date.AddDays(-1);
                    var from = _lastAbsentDate == DateTime.MinValue ? yesterday : _lastAbsentDate.Date.AddDays(1);
                    for (var d = from; d <= yesterday; d = d.AddDays(1))
                        await MarkAbsentAsync(DateOnly.FromDateTime(d));
                    _lastAbsentDate = now;
                }

                // 每月 1 号 00:10 之后是首选执行时间点（留几分钟缓冲，给设备重传/网络延迟一点时间，
                // 免得月末最后几分钟的打卡因为还没到账就被漏算进汇总）；如果这次因为异常/重启被错过，
                // 2、3 号任意时间都会自动补跑——GenerateMonthlySummaryAsync 本身是幂等的（重新算一遍
                // 只是覆盖同一份汇总，不会重复插入或误发通知），补跑不会有副作用。
                var isSummaryFirstWindow = now.Day == 1 && now.Hour == 0 && now.Minute >= 10;
                var isSummaryCatchUp     = now.Day is 2 or 3;
                if ((isSummaryFirstWindow || isSummaryCatchUp) && _lastSummaryDate.Date < new DateTime(now.Year, now.Month, 1))
                {
                    var prev = now.AddMonths(-1);   // 上个月
                    await GenerateSummaryAsync(prev.Year, prev.Month);
                    _lastSummaryDate = now;
                }

                // 每天 03:00 且今天还没清理过 → 清理考勤机过期数据 + 智能助手限流计数器里过期的天数
                // （AgentRateLimiter.CleanupExpired 以前没有任何地方调用，字典只增不减；量级很小，
                // 顺手在这里每天清一次，2026-09-29 审查发现 L3）
                if (now.Hour == 3 && _lastCleanupDate.Date < now.Date)
                {
                    await CleanupZKDeviceDataAsync();
                    await CleanupOldReminderNotificationsAsync();
                    AgentRateLimiter.CleanupExpired();
                    _lastCleanupDate = now;
                }

                // 距上次提醒过去满 4 小时（程序刚启动、_lastApprovalReminderAt 还是最小值时，
                // 差值必然超过 4 小时，所以启动后很快就会先跑一次，不用等真的攒够 4 小时）
                if (now - _lastApprovalReminderAt >= TimeSpan.FromHours(4))
                {
                    await RemindPendingApprovalsAsync();
                    _lastApprovalReminderAt = now;
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
    /// 扫描 <paramref name="today"/> 这一天所有在职员工：没记录/没打上班卡 → 旷工；打了上班卡但没打
    /// 下班卡 → 未打卡。节假日（以及非补班日的周末）跳过；对旷工/缺卡发提醒通知。参数化成任意日期
    /// 是为了给补跑用——正常每天 23:55-23:59 传的就是当天，补跑时传的是错过的历史日期。
    /// </summary>
    private async Task MarkAbsentAsync(DateOnly today)
    {
        // 后台任务里要自己开一个“作用域”来拿数据库（不能直接用构造函数注入的，生命周期不同）
        using var scope = scopeFactory.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();

        // 免考勤的人（管理员/文员/办公室人员等不需要打卡的账号）不参与自动记旷工/未打卡
        var users = await db.Users.Where(u => u.IsActive && !u.IsAttendanceExempt).ToListAsync();

        // 一次性把”今天已有的考勤记录””今天的排班”查出来放内存，循环里直接用，避免逐人查库（N+1）
        var recordByUser  = (await db.AttendanceRecords.Where(r => r.WorkDate == today).ToListAsync())
            .GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.First());
        // 今天排了班的人，连同班次一起取出来——用来判断"这个班次自己配置的每周休息日"是不是命中了今天
        var todayAssignmentByUser = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => a.WorkDate == today)
                .ToListAsync())
            .GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.First());

        int marked = 0;

        foreach (var user in users)
        {
            // 没办入职（没填入职日期、或入职日期还没到）的人不处理——跟 AttendanceService.PunchAsync
            // 里"没办入职不让打卡"的判断保持一致，不然还没入职的人会被莫名其妙标记旷工、还收到提醒。
            if (user.HireDate is null || user.HireDate.Value > today) continue;

            // 今天是不是"这个人的休息日"：优先看他自己排的班次配置了每周哪几天休息（比如三班倒可能休
            // 二、三，不是标准的周六周日；六天倒班可能周六照常上班），没排班时才退回到按自然周末判断——
            // 跟 AttendanceService.CountExpectedWorkdays/IsNonCompRestDayAsync 用同一个判断
            // （AttendanceService.IsShiftWeeklyRestDay），不再是"周六周日一律跳过"的粗口径。之前先判
            // 自然周末、周末直接跳过，六天倒班的人周六没来也没请假会被漏判旷工，"应出勤"却仍然算这天
            // （发现于 2026-09-18 数据核查）。
            todayAssignmentByUser.TryGetValue(user.Id, out var todayAssignment);
            if (AttendanceService.IsShiftWeeklyRestDay(today, todayAssignment?.ShiftSchedule)) continue;

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
            else if (record.ClockInTime is null && record.ClockOutTime is not null)
            {
                // 只有下班卡、没有上班卡：人确实到岗了（有打卡为证），只是漏打（或没打上）上班卡——记"未打卡（缺上班卡）"，
                // 不算旷工。这类记录以前多半来自"当天唯一一次很晚的打卡被当成上班卡"，现在设备同步会把下班时间
                // 之后的首次打卡按下班卡处理（见 ZKDeviceSyncService），就会落到这里。已经是未打卡的不重复发提醒。
                if (record.AttendanceStatus != AttendanceStatus.NotPunched)
                {
                    record.AttendanceStatus = AttendanceStatus.NotPunched;
                    record.UpdatedAt        = DateTime.Now;
                    db.Notifications.Add(new Notification
                    {
                        UserId           = user.Id,
                        Title            = "上班未打卡提醒",
                        Content          = $"您今日（{today:MM/dd}）未打上班卡，如有异议请提交补卡申请",
                        NotificationType = "PunchReminder",
                        CreatedAt        = DateTime.Now
                    });
                    marked++;
                }
            }
            else if (record.ClockInTime is null)
            {
                // 有记录但没打上班卡 → 旷工。跟"完全没记录"那个分支一样要发提醒（管理员只补了下班卡、
                // 会建出这种没上班卡的记录，当晚被判旷工员工却毫不知情）；已经是旷工的记录说明之前（补跑/
                // 重启重复执行时）已经处理过，不再重复发（2026-09-24 审查修复）
                if (record.AttendanceStatus != AttendanceStatus.Absent)
                {
                    db.Notifications.Add(new Notification
                    {
                        UserId           = user.Id,
                        Title            = "今日旷工提醒",
                        Content          = $"您今日（{today:MM/dd}）未打上班卡，已被标记为旷工，如有异议请提交补卡申请",
                        NotificationType = "PunchReminder",
                        CreatedAt        = DateTime.Now
                    });
                }
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

                // 已经标记过"未打卡"的记录不再重复处理/重复发提醒——_lastAbsentDate 是内存变量，
                // 应用如果恰好在 00:00-03:00 补跑窗口内重启，_lastAbsentDate 会归零，补跑逻辑会把
                // "昨天"重新标一遍，之前已经正确标成"未打卡"、也已经发过提醒的记录会被这里无条件
                // 再 Add 一条一模一样的 Notification，员工会收到重复提醒（2026-09-21 代码审查发现）
                if (record.AttendanceStatus == AttendanceStatus.NotPunched) continue;

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

        // 昨天及更早，是不是有夜班（跨天班次）打了上班卡、一直没打下班卡的记录——检查当天因为
        // "人可能还在上班、要到第二天凌晨才下班"特意跳过了（见上面 IsCrossDay 那个 continue）。
        // 现在已经过了至少一整天，如果还是没有下班卡，说明是真的漏打了（忘记打卡/离职/设备故障），
        // 需要在这里补上标记——不然这条记录会永远停在"已上班未下班"，旷工/未打卡看板永远看不到、
        // 也永远收不到提醒（因为后续每天的检查只看"今天"的记录，不会再回头看这条）。
        // 用 "< today" 而不是只查 "== 昨天"：服务如果连续停机/宕机跨越了两个以上的午夜，早于昨天的
        // 未闭合记录不会因为只被检查漏过一次就从此再也追不上，这里会把它们都一起补标。
        var activeUserIds = users.Select(u => u.Id).ToHashSet();   // 复用上面已查好的"当前在职员工"名单
        var openRecords = (await db.AttendanceRecords
            .Where(r => r.WorkDate < today && r.ClockInTime != null && r.ClockOutTime == null
                     && r.AttendanceStatus != AttendanceStatus.NotPunched
                     && r.AttendanceStatus != AttendanceStatus.OnLeave
                     && r.AttendanceStatus != AttendanceStatus.Holiday
                     && r.AttendanceStatus != AttendanceStatus.BusinessTrip)
            .ToListAsync())
            .Where(r => activeUserIds.Contains(r.UserId))   // 已离职/停用的人不再标记、不再发提醒
            .ToList();
        if (openRecords.Count > 0)
        {
            var openUserIds = openRecords.Select(r => r.UserId).Distinct().ToList();
            var openDates   = openRecords.Select(r => r.WorkDate).Distinct().ToList();
            var assignByUserDate = (await db.ShiftAssignments
                    .Include(a => a.ShiftSchedule)
                    .Where(a => openUserIds.Contains(a.UserId) && openDates.Contains(a.WorkDate))
                    .ToListAsync())
                .ToDictionary(a => (a.UserId, a.WorkDate));

            foreach (var record in openRecords)
            {
                // 只处理"当天排的确实是跨天班次"这种情况——普通白班漏打下班卡当天就已经处理过了，
                // 不会走到这里；这里只是给夜班这一类"故意延后再判定"的情况兜底。
                if (!assignByUserDate.TryGetValue((record.UserId, record.WorkDate), out var assign) || !assign.ShiftSchedule.IsCrossDay)
                    continue;

                record.AttendanceStatus = AttendanceStatus.NotPunched;
                record.UpdatedAt        = DateTime.Now;
                db.Notifications.Add(new Notification
                {
                    UserId           = record.UserId,
                    Title            = "下班未打卡提醒",
                    Content          = $"您 {record.WorkDate:MM/dd} 的夜班一直未打下班卡，如有异议请提交补卡申请",
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
    /// ①-b 超过 RetentionDays 天、还是没确认的命令——不管是重试次数用完被标记 Failed 的，还是纯粹一直没等到
    ///    设备确认的，堆着不清也没有意义（Failed 的已经不会再下发，没 Failed 的也已经很旧了），一并清掉，
    ///    避免命令表随时间无限膨胀；
    /// ② 超过 RetentionDays 天的考勤照片（ATTPHOTO）和远程打卡现场照片——目前都是只写不读的留痕数据，
    ///    放着只会一直占磁盘；
    /// ③ 超过 RetentionDays 天的人脸识别尝试记录（FaceVerifyAttempt）——只在限流查询里用到最近几分钟内的，
    ///    更早的没有查询价值。
    /// 保留天数和 Serilog 日志一致（30 天），不给运维增加新的心智负担。
    /// </summary>
    /// <summary>
    /// 给"挂了 4 小时以上还没处理"的待审批申请，往当前轮到处理的审批人发提醒。一张申请单可能配了多级审批，
    /// 同一时刻只有一个节点是"轮到你了"（StepOrder 最小、还是待审批状态的那个），跟
    /// ApprovalService.GetMyPendingApprovalsAsync 用的是同一套"当前节点"逻辑，避免提醒了还没轮到的后一级。
    /// 口径：
    /// ① 只算 UpdatedAt（提交或每一级审批时都会更新）在 4 小时以前的单，刚提交/刚流转到这一级的不打扰——
    ///    不能指望"这个任务每 4 小时跑一次就等于挂了 4 小时"，程序刚启动/重启后会立刻先跑一次；
    /// ② 跳过已停用的审批人、以及申请人调岗后已经管不到的审批人（跟 ApproverCoversApplicantAsync/
    ///    GetPendingForApproverAsync 同一个口径），这类"审批人事实上处理不了"的单不再徒劳提醒；
    /// ③ 每个审批人每一轮只发一条汇总通知（"您有 N 张待审批……最久的已挂 X"），不是每张单一条。
    ///    2026-10-06 检查生产发现：积压了 2400 多张待审批单（大多是 9 月中旬起的加班单），按单发的话每天
    ///    新增 1.3 万条通知，积压最多的审批人每 4 小时会被弹出十几张卡片、响铃，通知表三天涨到 8 万多行；
    /// ④ 发完新汇总，把所有"更早的、还没读的审批提醒"（上一轮的汇总、改造前按单发的旧提醒）标成已读——
    ///    每个人未读的提醒始终只有最新一条；提交/流转时发的事件通知（标题不同）不动。每一轮都是一条
    ///    "新出现的"通知，前端轮询才能识别成新通知、触发弹窗和提示音（早先做成"已有未读就跳过"，
    ///    结果审批人只要没点开最早那条通知就永远收不到提醒，已改掉）。先插入新的、再标旧的已读，
    ///    万一中途失败最多是多一条重复提醒，不会丢提醒。
    /// </summary>
    private const string ReminderTitle = "审批提醒";

    private async Task RemindPendingApprovalsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db         = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
        var deptScope  = scope.ServiceProvider.GetRequiredService<IDeptScopeService>();

        var now    = DateTime.Now;
        var cutoff = now.AddHours(-4);
        var openRequests = await db.ApprovalRequests
            .Where(r => (r.ApprovalStatus == ApprovalStatus.Pending || r.ApprovalStatus == ApprovalStatus.InProgress)
                     && r.UpdatedAt <= cutoff)
            .Include(r => r.Applicant)
            .Include(r => r.ApprovalSteps)
            .ToListAsync();

        // 当前轮到谁处理，一张单最多一个人；先批量把这些审批人的"在职状态/管理范围"查出来，
        // 不在循环里逐条查数据库
        var currentSteps = openRequests
            .Select(r => r.ApprovalSteps.Where(s => s.ApprovalStatus == ApprovalStatus.Pending).OrderBy(s => s.StepOrder).FirstOrDefault())
            .Where(s => s is not null)
            .Cast<ApprovalStep>()
            .ToList();
        var approverIds = currentSteps.Select(s => s.ApproverUserId).ToHashSet();
        var approvers = await db.Users.Where(u => approverIds.Contains(u.Id))
            .Select(u => new { u.Id, u.IsActive, u.ScopedDepartmentId })
            .ToDictionaryAsync(u => u.Id);

        // 部门子树按"审批人的管理范围根部门"缓存，同一个范围根不用重复算（GetSubtreeIdsAsync 每次都会
        // 把全部门表扫一遍，审批人可能有好几个人共用同一个范围根，缓存能省掉重复的数据库往返）
        var subtreeCache = new Dictionary<int, HashSet<int>>();
        async Task<bool> CoversApplicantAsync(int scopedDeptId, int? applicantDeptId)
        {
            if (applicantDeptId is null) return false;   // 申请人没有部门归属，只总部可见
            if (!subtreeCache.TryGetValue(scopedDeptId, out var ids))
            {
                ids = await deptScope.GetSubtreeIdsAsync(scopedDeptId);
                subtreeCache[scopedDeptId] = ids;
            }
            return ids.Contains(applicantDeptId.Value);
        }

        // 每个审批人：这一轮需要提醒的单子数 + 其中挂得最久的那张的"挂起起点"
        var perApprover = new Dictionary<int, (int Count, DateTime Oldest)>();
        foreach (var req in openRequests)
        {
            var currentStep = req.ApprovalSteps
                .Where(s => s.ApprovalStatus == ApprovalStatus.Pending)
                .OrderBy(s => s.StepOrder)
                .FirstOrDefault();
            if (currentStep is null) continue;   // 理论上不会发生：整单还是 Pending/InProgress 就一定有一个待处理节点，这里只是防御性判断

            if (!approvers.TryGetValue(currentStep.ApproverUserId, out var approver) || !approver.IsActive)
                continue;   // 审批人账号不存在或已停用，提醒了也没人处理
            if (approver.ScopedDepartmentId.HasValue
                && !await CoversApplicantAsync(approver.ScopedDepartmentId.Value, req.Applicant.DepartmentId))
                continue;   // 申请人调岗后，原审批人已经管不到了（这类卡住的单交给总部处理，不在这里提醒）

            var id = currentStep.ApproverUserId;
            perApprover[id] = perApprover.TryGetValue(id, out var cur)
                ? (cur.Count + 1, req.UpdatedAt < cur.Oldest ? req.UpdatedAt : cur.Oldest)
                : (1, req.UpdatedAt);
        }

        if (perApprover.Count > 0)
        {
            db.Notifications.AddRange(perApprover.Select(kv =>
            {
                var age     = now - kv.Value.Oldest;
                var ageText = age.TotalHours >= 24 ? $"{(int)age.TotalDays} 天" : $"{Math.Max(1, (int)age.TotalHours)} 小时";
                return new Notification
                {
                    UserId           = kv.Key,
                    Title            = ReminderTitle,
                    Content          = $"您有 {kv.Value.Count} 张待审批申请已超过 4 小时没处理，其中最久的已挂 {ageText}，请及时审批",
                    NotificationType = "ApprovalPending",
                    RelatedId        = null,   // 汇总通知不对应某一张单；点击后统一跳转到"待我审批"列表
                    CreatedAt        = now
                };
            }));
            await db.SaveChangesAsync();
        }

        // 不管这一轮有没有人要提醒，都把"更早的"未读提醒清掉：没人要提醒的审批人（积压已经处理完了），
        // 之前那条"您有 N 张待审批"也已经过时，不该一直挂在未读里
        await db.Notifications
            .Where(n => n.NotificationType == "ApprovalPending" && n.Title == ReminderTitle && !n.IsRead && n.CreatedAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true).SetProperty(n => n.ReadAt, now));
    }

    /// <summary>
    /// 清掉 7 天前、已经读过的"审批提醒"：每 4 小时一条汇总，不清的话通知表会一直涨（2026-10-06 检查时
    /// 改造前按单发的旧提醒已经积了 8 万多行）。只清提醒，不动提交/流转/审批结果这类事件通知，
    /// 也不动没读的（没读的提醒会在下一轮被标成已读，第二天的清理就会带走）。
    /// </summary>
    private async Task CleanupOldReminderNotificationsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
        var cutoff = DateTime.Now.AddDays(-7);
        var deleted = await db.Notifications
            .Where(n => n.NotificationType == "ApprovalPending" && n.Title == ReminderTitle && n.IsRead && n.CreatedAt < cutoff)
            .ExecuteDeleteAsync();
        if (deleted > 0) logger.LogInformation("已清理 {Count} 条 7 天前已读的审批提醒通知", deleted);
    }

    private async Task CleanupZKDeviceDataAsync()
    {
        const int retentionDays = 30;
        var cutoff = DateTime.Now.AddDays(-retentionDays);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();

        var deletedConfirmedCommands = await db.ZKDeviceCommands
            .Where(c => c.Confirmed && c.ConfirmedAt != null && c.ConfirmedAt < cutoff)
            .ExecuteDeleteAsync();

        var deletedStaleUnconfirmedCommands = await db.ZKDeviceCommands
            .Where(c => !c.Confirmed && c.CreatedAt < cutoff)
            .ExecuteDeleteAsync();

        var deletedAttempts = await db.FaceVerifyAttempts
            .Where(a => a.CreatedAt < cutoff)
            .ExecuteDeleteAsync();

        var deletedZkPhotoDirs   = CleanupOldDateDirs(scope, "zkdevice", cutoff);
        var deletedFacePhotoDirs = CleanupOldDateDirs(scope, Path.Combine("faces", "attempts"), cutoff);

        logger.LogInformation(
            "考勤机/远程打卡数据清理完成：删除已确认命令 {CmdCount} 条，删除长期未确认命令 {StaleCmdCount} 条，" +
            "删除人脸尝试记录 {AttemptCount} 条，删除考勤照片目录 {ZkDirCount} 个，删除远程打卡照片目录 {FaceDirCount} 个",
            deletedConfirmedCommands, deletedStaleUnconfirmedCommands, deletedAttempts, deletedZkPhotoDirs, deletedFacePhotoDirs);
    }

    /// <summary>删掉 PrivateUploads/{UploadPath}/{subPath} 下文件夹名能解析成日期、且早于 cutoff 的整个目录
    /// （目录名格式是 yyyyMMdd，按文件夹名判断即可，不用挨个读文件的创建时间）。</summary>
    private static int CleanupOldDateDirs(IServiceScope scope, string subPath, DateTime cutoff)
    {
        var env        = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var appOptions = scope.ServiceProvider.GetRequiredService<IOptions<AppSettingsOptions>>().Value;

        var uploadPath = appOptions.UploadPath.Trim('/', '\\');
        var root       = Path.Combine(PrivateFileStorage.GetRoot(env), uploadPath, subPath);
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
