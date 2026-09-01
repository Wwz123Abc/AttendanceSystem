using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 熵基考勤机打卡数据落库：原始打卡流水去重写入 + 按人/日 upsert 考勤日记录，
/// 上班/下班按"当天第一次算上班、之后都算下班"自动判断（不少机型没有签到/签退按键，
/// 设备上报的状态不可靠），迟到/早退状态当场按班次计算。
/// </summary>
public class ZKDeviceSyncService(AttendanceDbContext db, ILogger<ZKDeviceSyncService> logger) : IZKDeviceSyncService
{
    private const int MaxAttempts = 5;

    /// <summary>上班打卡之后至少要隔这么多分钟，后续打卡才有资格被当成"下班"候选——
    /// 防止人脸识别失败重扫、或几十秒内又碰了一下设备，被误判成"秒下班"。</summary>
    private const int MinMinutesBeforeClockOut = 5;

    /// <summary>
    /// 外层套一层重试：高峰期设备可能因为网络抖动/服务器响应慢而重发同一批数据，两个几乎同时到达的请求
    /// 都以为"这条考勤日记录还不存在"就都想新建，数据库的唯一约束（同一人同一天只能一条；以及同一人同类型
    /// 同一分钟只能一条打卡流水）会拦住后到的那个、抛出 DbUpdateException，导致这一整批 SaveChanges 失败。
    /// 这种情况直接重新读一遍最新数据重跑一次就能处理好（第二次跑的时候后到的那次会看到记录已存在，走更新
    /// 分支），不是脏数据问题，最多重试 5 次（打卡高峰期冲突概率比平时高，多给点缓冲）。5 次仍然冲突的话，
    /// 异常会继续往上抛给 ZKDeviceController.Upload，那边会告诉设备这次没传成功，让设备自己重传这批数据，
    /// 不会静默丢数据。
    /// </summary>
    public async Task ProcessAttLogAsync(string sn, List<ZKAttLogRow> rows, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await ProcessAttLogCoreAsync(sn, rows, ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "考勤机 {SN} 打卡数据落库遇到并发冲突，第 {Attempt} 次重试", sn, attempt);
                db.ChangeTracker.Clear();   // 丢弃这次没保存成功的改动，下一轮重新从数据库读最新状态
            }
        }
    }

    private async Task ProcessAttLogCoreAsync(string sn, List<ZKAttLogRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;

        // 1) 设备侧的 PIN 就是本系统的工号（EmployeeNo），查出对应的本地用户
        var pins = rows.Select(r => r.Pin).Distinct().ToList();
        var users = await db.Users
            .Where(u => pins.Contains(u.EmployeeNo))
            .Select(u => new { u.Id, u.EmployeeNo, u.AttendanceGroupId })
            .ToListAsync(ct);
        var userByPin     = users.ToDictionary(u => u.EmployeeNo, u => u.Id);
        var groupIdByUser = users.ToDictionary(u => u.Id, u => u.AttendanceGroupId);

        var unmatched = pins.Where(p => !userByPin.ContainsKey(p)).ToList();
        if (unmatched.Count > 0)
            logger.LogWarning("考勤机 {SN} 推送的打卡记录里，有工号在系统里找不到对应员工：{Pins}", sn, string.Join(",", unmatched));

        var uids  = userByPin.Values.ToHashSet();
        // 也要预加载每个打卡日期的"前一天"：夜班（跨天班次）下班卡打卡时日历已经翻到第二天，
        // 需要能查到"昨天"的记录/排班才能判断这次打卡是不是续上昨晚那个还没打下班卡的夜班。
        var dates = rows.Select(r => DateOnly.FromDateTime(r.Time))
            .SelectMany(d => new[] { d, d.AddDays(-1) })
            .Distinct().ToList();
        if (uids.Count == 0) return;

        // 2) 预加载：考勤组午休/晚餐扣时、已有打卡流水（去重用）、已有考勤日记录、当天排班
        var groupBreaks = await db.AttendanceGroups
            .Select(g => new { g.Id, g.LunchBreakMinutes, g.DinnerBreakMinutes })
            .ToDictionaryAsync(g => g.Id, g => (g.LunchBreakMinutes, g.DinnerBreakMinutes), ct);

        var punchSet = (await db.AttendancePunches
                .Where(p => uids.Contains(p.UserId) && dates.Contains(DateOnly.FromDateTime(p.PunchTime)))
                .Select(p => new { p.UserId, p.PunchType, p.PunchTime })
                .ToListAsync(ct))
            .Select(p => (p.UserId, p.PunchType, Minute: TruncateToMinute(p.PunchTime)))
            .ToHashSet();

        var recordMap = (await db.AttendanceRecords
                .Where(r => uids.Contains(r.UserId) && dates.Contains(r.WorkDate))
                .ToListAsync(ct))
            .ToDictionary(r => (r.UserId, r.WorkDate));

        var shiftByUserDate = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => uids.Contains(a.UserId) && dates.Contains(a.WorkDate))
                .ToListAsync(ct))
            .ToDictionary(a => (a.UserId, a.WorkDate), a => a.ShiftSchedule);

        // 3) 逐条处理
        var touchedKeys = new HashSet<(int UserId, DateOnly WorkDate)>();   // 这一批实际动过的 (人, 日期)，
                                                                             // 下面第 4 步只用重新算这些，不用整批全扫
        foreach (var r in rows.OrderBy(r => r.Time))   // 按时间顺序处理，保证"当天第一次算上班、之后都算下班"取值正确
        {
            if (!userByPin.TryGetValue(r.Pin, out var uid)) continue;

            var calendarDate = DateOnly.FromDateTime(r.Time);
            var workDate     = calendarDate;

            // 夜班（跨天班次）下班卡打卡时日历已经翻到第二天：优先续上"昨天已打上班卡、还没打下班卡"的记录，
            // 但只在昨天排的确实是跨天班次时才续，避免把普通白班忘打下班卡的旧记录误接到今天的打卡上
            // （逻辑和 AttendanceService.PunchAsync 的夜班续接处理保持一致）。
            var yesterday = calendarDate.AddDays(-1);
            if (recordMap.TryGetValue((uid, yesterday), out var yesterdayRecord)
                && yesterdayRecord.ClockInTime != null && yesterdayRecord.ClockOutTime == null
                && shiftByUserDate.TryGetValue((uid, yesterday), out var yesterdayShift)
                && yesterdayShift?.IsCrossDay == true)
            {
                workDate = yesterday;
            }

            if (!recordMap.TryGetValue((uid, workDate), out var record))
            {
                record = new AttendanceRecord { UserId = uid, WorkDate = workDate };
                db.AttendanceRecords.Add(record);
                recordMap[(uid, workDate)] = record;
            }

            shiftByUserDate.TryGetValue((uid, workDate), out var shift);

            // 不少机型没有签到/签退按键（或员工不会用），设备上报的 Status 不可靠，改成不看 Status、
            // 按班次配置自动判断：当天第一次算上班；之后如果落在班次配置的"午间必打卡"窗口内，算午间
            // 打卡（不影响上下班时间）；不在任何窗口内，才算下班。这样员工上班期间随手多刷几次脸
            // 也不会把午间打卡误记成下班时间。外出/外出返回（Status 2、3）设备上报明确，仍按设备说的走。
            // 另外：刚打完上班卡没多久（比如人脸识别失败、几十秒内又扫了一次）不能当成下班——
            // 必须离上班时间超过 MinMinutesBeforeClockOut 才有资格被当成"下班"候选。
            var type = r.Status switch
            {
                2 or 3 => PunchType.MidCheck,
                _ => record.ClockInTime is null ? PunchType.ClockIn
                    : r.Time - record.ClockInTime.Value < TimeSpan.FromMinutes(MinMinutesBeforeClockOut) ? PunchType.ClockIn
                    : AttendanceService.IsWithinAnyMidCheckWindow(r.Time, workDate, shift) ? PunchType.MidCheck
                    : PunchType.ClockOut
            };

            if (!punchSet.Add((uid, type, TruncateToMinute(r.Time)))) continue;   // 去重：同一人同类型同一分钟已经存过就跳过
            touchedKeys.Add((uid, workDate));

            db.AttendancePunches.Add(new AttendancePunch
            {
                UserId     = uid,
                PunchTime  = TruncateToMinute(r.Time),   // 和 App 打卡（AttendanceService.PunchAsync）一样精确到分钟，
                                                          // 落库值和上面 punchSet 去重键、数据库唯一索引三处口径一致
                PunchType  = type,
                DeviceInfo = $"ZKDevice:{sn}",
                IsValid    = true,
                CreatedAt  = DateTime.Now
            });

            if (type == PunchType.ClockIn && (record.ClockInTime is null || r.Time < record.ClockInTime))
            {
                record.ClockInTime = r.Time;
                var status = AttendanceService.CalcClockInStatus(r.Time, shift, out var lateMin);
                record.LateMinutes = lateMin;
                if (status == AttendanceStatus.Late) record.AttendanceStatus = AttendanceStatus.Late;
            }
            else if (type == PunchType.ClockOut && (record.ClockOutTime is null || r.Time > record.ClockOutTime))
            {
                // 员工上班期间可能会因为各种原因（午间打卡、中途路过设备等）随手多刷几次脸，
                // 这些"随手打卡"按"取最晚"规则会暂时被当成下班时间——这里没问题，反正之后真正
                // 下班再打一次就会被覆盖成正确值。但早退状态如果只加不减，会导致中途一次随手打卡
                // 被判成"早退"之后，哪怕后面真的按时/晚走了，这个错误的"早退"标记也摘不掉。
                // 所以这里改成每次更新下班时间都重新完整评估一次状态，而不是只加不减；
                // 只在当天状态还是"正常/早退/未打卡"这种由打卡本身决定的状态时才重新评估——
                // "未打卡"也要能被覆盖：这次既然真的收到了下班打卡，就不再是"未打卡"了，
                // 不然后台定时任务标过一次"未打卡"之后，哪怕后面设备补传了正常的下班卡，
                // 状态也会永远卡在"未打卡"改不回来。请假/出差/节假日/旷工/迟到这些由审批流程、
                // 定时任务或上班打卡设置的状态，优先级更高，不能被这里的下班打卡同步顺手覆盖掉。
                record.ClockOutTime = r.Time;
                var status = AttendanceService.CalcClockOutStatus(workDate, r.Time, shift, out var earlyMin);
                record.EarlyLeaveMinutes = earlyMin;
                if (record.AttendanceStatus is AttendanceStatus.Normal or AttendanceStatus.EarlyLeave or AttendanceStatus.NotPunched)
                    record.AttendanceStatus = status;
            }

            record.Remark    = "熵基考勤机同步";
            record.UpdatedAt = DateTime.Now;
        }

        // 4) 工时结算：每个这一批动过的 (人, 日期) 只结算一次（不放进上面的循环里，是因为同一人当天
        // 可能有好几条打卡，放循环里会每条都重新查一遍全天打卡记录、重复算好几遍，浪费数据库查询）。
        foreach (var (uid, workDate) in touchedKeys)
        {
            var record = recordMap[(uid, workDate)];
            if (record.ClockInTime is not { } ci || record.ClockOutTime is not { } co || co <= ci) continue;

            shiftByUserDate.TryGetValue((uid, workDate), out var shift);
            var (lunch, dinner) = groupIdByUser.TryGetValue(uid, out var gid) && gid.HasValue
                                  && groupBreaks.TryGetValue(gid.Value, out var brk)
                ? brk : (60, 30);

            // 漏打"午间必打卡"窗口要顺延有效上班时间，跟本地打卡（ResolveEffectiveClockInAsync）
            // 走的是同一套算法，不然同样"漏打午间卡"这件事，考勤机同步和本地打卡算出来的工时会对不上；
            // 命中情况也要写回 record.MidCheckResults，不然"我的记录"页看不到午间打卡的命中详情。
            var missedWindowEnds = new List<DateTime>();
            var windows = shift?.ParseMidCheckWindows() ?? [];
            if (shift is not null && windows.Count > 0)
            {
                var dayPunchTimes = await db.AttendancePunches
                    .Where(p => p.UserId == uid
                             && p.PunchTime >= workDate.ToDateTime(TimeOnly.MinValue).AddDays(-1)
                             && p.PunchTime <= workDate.ToDateTime(TimeOnly.MinValue).AddDays(2))
                    .Select(p => p.PunchTime)
                    .ToListAsync(ct);
                // 这一批里刚 Add 但还没 SaveChanges 的打卡，数据库还查不到，要单独补进去
                dayPunchTimes.AddRange(db.AttendancePunches.Local
                    .Where(p => p.UserId == uid)
                    .Select(p => p.PunchTime));
                var midCheckResults = AttendanceService.ResolveMidCheckResults(workDate, shift, windows, dayPunchTimes.Distinct().ToList());
                record.MidCheckResults = midCheckResults.FormatMidCheckResults();
                missedWindowEnds = midCheckResults.Where(m => !m.IsSatisfied)
                    .Select(m => AttendanceService.ResolveShiftTime(workDate, m.WindowEnd, shift)).ToList();
            }

            var effectiveClockIn  = AttendanceService.ClampEffectiveClockIn(workDate, ci, shift, missedWindowEnds);
            var effectiveClockOut = AttendanceService.ClampEffectiveClockOut(workDate, co, shift);
            record.ActualWorkHours = AttendanceService.ComputeWorkHours(effectiveClockIn, effectiveClockOut, lunch, dinner);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 给白名单里的每台设备都排一条"下发员工信息"命令，下次设备心跳（/iclock/getrequest）时会被取走。
    /// 命令格式参考熵基官方 PUSH 协议参考实现（Demo-Java 的 GenerateCmd）：DATA UPDATE USERINFO，
    /// 字段用 Tab 分隔，PIN 直接用本系统的工号（EmployeeNo）——这样设备推上来的打卡记录才能按工号对上人。
    /// </summary>
    public async Task EnqueuePushUserInfoAsync(User user, CancellationToken ct = default)
    {
        var snList = await db.ZKDevices.Where(d => d.IsActive).Select(d => d.SN).ToListAsync(ct);
        if (snList.Count == 0 || string.IsNullOrWhiteSpace(user.EmployeeNo)) return;

        var name = user.RealName.Replace('\t', ' ');   // 名字里不该有 Tab，保险起见替换掉，避免破坏字段分隔
        var commandText = $"DATA UPDATE USERINFO PIN={user.EmployeeNo}\tName={name}\tPri=0\tPasswd=\tCard=\tGrp=1\tTZ=0000000000000000\tVerify=0\tViceCard=";

        foreach (var sn in snList)
        {
            db.ZKDeviceCommands.Add(new ZKDeviceCommand { SN = sn, CommandText = commandText });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>给白名单里的每台设备都排一条"删除该工号"命令（DATA DELETE USERINFO），
    /// 用在员工离职/被彻底删除、或者工号被改掉（旧工号在设备上就该清掉）这几个场景。</summary>
    public async Task EnqueueDeleteUserInfoAsync(string employeeNo, CancellationToken ct = default)
    {
        var snList = await db.ZKDevices.Where(d => d.IsActive).Select(d => d.SN).ToListAsync(ct);
        if (snList.Count == 0 || string.IsNullOrWhiteSpace(employeeNo)) return;

        var commandText = $"DATA DELETE USERINFO PIN={employeeNo}";
        foreach (var sn in snList)
        {
            db.ZKDeviceCommands.Add(new ZKDeviceCommand { SN = sn, CommandText = commandText });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>去重粒度用"分钟"而不是"秒"：同一人同一分钟内多次刷卡（设备防抖间隔内的重复上报）
    /// 只记一条，避免原始打卡流水表里出现同一次打卡被拆成两条秒数不同的记录。</summary>
    private static DateTime TruncateToMinute(DateTime dt) => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0);
}
