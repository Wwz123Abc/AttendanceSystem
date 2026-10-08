using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>打卡主流程：上班/下班判定、定位校验、跨天与补卡的时间规则（<see cref="AttendanceService"/> 的一部分）。</summary>
public partial class AttendanceService
{
    /// <summary>
    /// 员工打卡（上班/下班）。先查后插（同一分钟内是否已经打过卡）本身有 TOCTOU 窗口：两个几乎同时
    /// 到达的请求（比如远程打卡同一分钟内被点了两次、或者点了没反应又点了一次）都会以为"这一分钟还没打过"，
    /// 后到的那个插入打卡流水时会撞到数据库的唯一索引，抛出 DbUpdateException 导致这次打卡直接 500。
    /// 跟 <see cref="Implementations.ZKDeviceSyncService.ProcessAttLogAsync"/> 用同一套处理方式：外层套一层
    /// 重试，冲突了就清空这次没保存成功的改动、重新读一遍最新数据重跑一次（第二次会看到流水已经存在，
    /// 直接跳过插入），不是脏数据问题，最多重试 5 次。
    /// </summary>
    public async Task<PunchResponseDto> PunchAsync(int userId, PunchRequestDto request, bool skipLocationCheck = false)
    {
        for (var attempt = 1; attempt <= MaxPunchAttempts; attempt++)
        {
            try
            {
                return await PunchCoreAsync(userId, request, skipLocationCheck);
            }
            catch (DbUpdateException ex) when (attempt < MaxPunchAttempts)
            {
                logger.LogWarning(ex, "打卡写入冲突，第 {Attempt} 次重试（用户 {UserId}）", attempt, userId);
                db.ChangeTracker.Clear();   // 丢弃这次没保存成功的改动，下一轮重新从数据库读最新状态
            }
            catch (Exception ex)
            {
                // 以前这里完全没有日志——重试耗尽或遇到非 DbUpdateException 的异常时，唯一调用方
                // RemotePunch.cshtml.cs 会把 ex.Message（EF 原始异常文本）直接显示给员工，运维这边
                // 却查不到任何真实原因（2026-09-21 代码审查发现）。这里补上日志再往上抛，
                // 调用方改成显示统一的友好文案，不再把内部异常细节暴露给员工。
                logger.LogError(ex, "打卡失败，用户 {UserId}", userId);
                throw;
            }
        }
        // 理论上到不了这里：循环最后一次要么 return，要么让异常继续往上抛
        throw new BusinessException("打卡失败，请重试");
    }

    private async Task<PunchResponseDto> PunchCoreAsync(int userId, PunchRequestDto request, bool skipLocationCheck)
    {
        var now   = clock.LocalNow();
        var today = DateOnly.FromDateTime(now);

        var user = await db.Users.FindAsync(userId)
            ?? throw new KeyNotFoundException("用户不存在");

        // 没办入职（没填入职日期或还没到入职日）不让打卡，避免产生异常数据
        if (user.HireDate is null || user.HireDate.Value > today)
            return new PunchResponseDto { Success = false, Message = "您尚未办理入职（入职日期未设置或未到），暂不能打卡，请联系管理员" };

        // 打卡时间精确到分钟（把秒抹掉）。提前到这里算，是因为下面的去重判断需要用它。
        var punchTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);

        // 同一人同类型同一分钟的重复提交（网络重试、按钮连点两下）：必须在这里就整个短路返回，
        // 不能等下面"这次打卡该续到昨天还是算今天新的一天"判断完、工时也结算完了才去重——
        // 第一次请求会把跨天夜班续到昨天、关掉昨天那条记录（下班时间不再是 null）；如果第二次
        // 重复请求不在这里拦掉，它会因为"昨天已经关闭"而判断不成立，退化成算作今天的打卡，
        // 把昨天的下班时间错误地写进今天的新记录（2026-09-30 从李杰的记录里发现的真实事故）。
        if (await db.AttendancePunches.AnyAsync(p =>
                p.UserId == userId && p.PunchType == request.PunchType && p.PunchTime == punchTime))
        {
            return new PunchResponseDto
            {
                Success   = true,
                Message   = request.PunchType switch
                {
                    PunchType.ClockIn  => "上班打卡成功",
                    PunchType.ClockOut => "下班打卡成功",
                    _                  => "午间打卡成功"
                },
                PunchTime = punchTime
            };
        }

        var (workDate, openYesterdayRecord) = await ResolvePunchWorkDateAsync(userId, request.PunchType, today, now);

        // 上班卡的合理性校验（夜班下班后重复刷、跨天班次打得太早）：远程打卡页面已经在调付费的人脸识别之前判过一次，
        // 这里是权威兜底，别的调用方也不会漏掉
        if (request.PunchType == PunchType.ClockIn && await GetClockInRejectionAsync(userId, now) is { } rejection)
            return new PunchResponseDto { Success = false, Message = rejection };

        // 定位打卡校验：没分配考勤组、或考勤组没开定位打卡/没配打卡地点，都算不通过（见 ValidateLocationAsync 注释）。
        // skipLocationCheck=true（远程打卡）时整段跳过——远程打卡自己会在调用这个方法之前先调
        // ValidateLocationAsync 做过一次同样的校验了（见该方法注释），这里不用再查一遍数据库重复判断。
        if (!skipLocationCheck)
        {
            var (locationValid, locationMessage) =
                await ValidateLocationAsync(user.AttendanceGroupId, request.Latitude, request.Longitude, request.Accuracy);
            if (!locationValid)
                return new PunchResponseDto { Success = false, Message = locationMessage! };
        }

        // 取 workDate 那天的排班，进而拿到班次（用来判断迟到/早退/加班）
        var assignment = await GetShiftAssignmentAsync(userId, workDate);
        var shift      = assignment is not null
            ? await db.ShiftSchedules.FindAsync(assignment.ShiftScheduleId)
            : null;
        // 今天是不是这个员工的休息日：休息日没有"应上班/应下班时间"可比，不该判迟到/早退
        // （调班补班日不算休息日，仍按班次时间正常判断）
        var isRestDay = IsNonCompRestDay(workDate, shift);

        // 第 3 步：写一条原始打卡流水（时间仍然是打卡的真实时刻，不受 workDate 影响）。
        // 同一人同类型同一分钟只能有一条（数据库唯一索引兜底），远程打卡这类"可以反复打"的场景
        // 同一分钟内点第二次很常见，这里先查一下是不是已经有了，有就不重复插入，避免撞唯一索引报错——
        // 不影响下面照常按这次的打卡时间刷新 ClockIn/ClockOutTime 和考勤状态。
        var alreadyPunchedThisMinute = await db.AttendancePunches.AnyAsync(p =>
            p.UserId == userId && p.PunchType == request.PunchType && p.PunchTime == punchTime);
        if (!alreadyPunchedThisMinute)
        {
            db.AttendancePunches.Add(new AttendancePunch
            {
                UserId     = userId,
                PunchTime  = punchTime,
                PunchType  = request.PunchType,
                Latitude   = request.Latitude,
                Longitude  = request.Longitude,
                Address    = request.Address,
                DeviceInfo = request.DeviceInfo,
                IsValid    = true,
                CreatedAt  = now
            });
        }

        // 第 4 步：取出 workDate 那天的考勤日记录，没有就新建一条（并填上应上/应下班时间）
        var record = openYesterdayRecord ?? await db.AttendanceRecords
            .FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == workDate);

        if (record is null)
        {
            record = new AttendanceRecord { UserId = userId, WorkDate = workDate };
            if (shift is not null)
            {
                record.ScheduledStartTime = workDate.ToDateTime(shift.WorkStartTime);
                record.ScheduledEndTime   = workDate.ToDateTime(shift.WorkEndTime);
                if (shift.IsCrossDay) record.ScheduledEndTime = record.ScheduledEndTime.Value.AddDays(1);  // 夜班顺延到第二天
            }
            db.AttendanceRecords.Add(record);
        }

        string message;
        AttendanceStatus status;
        int?   lateMinutes = null;

        if (request.PunchType == PunchType.ClockIn)   // ── 上班卡 ──
        {
            (status, lateMinutes, message) = ApplyClockIn(record, workDate, punchTime, shift, isRestDay);
        }
        else if (request.PunchType == PunchType.MidCheck)   // ── 午间打卡 ──
        {
            // 打卡流水已经在上面写好了；这里不改上下班时间/状态，工时结算在下班打卡时统一算（见 ResolveEffectiveClockInAsync）
            status  = record.AttendanceStatus;
            message = "午间打卡成功";
        }
        else                                          // ── 下班卡 ──
        {
            (status, message) = await ApplyClockOutAsync(record, user, workDate, punchTime, shift, isRestDay);
        }

        // 午间必打卡的命中情况要在打完这张卡就写回记录：以前只有打下班卡算工时时才顺带写（ResolveEffectiveClockInAsync），
        // 所以员工中午打完卡，"我的记录"里"午间打卡"一栏一直是 --，要等到下班打了卡才出现。下班卡那条路径自己会重算，这里不用管
        if (request.PunchType != PunchType.ClockOut
            && await LoadMidCheckResultsAsync(userId, workDate, shift) is { } midResults)
            record.MidCheckResults = midResults.FormatMidCheckResults();

        record.UpdatedAt = now;
        await db.SaveChangesAsync();

        // 同步刷新这个月的月度汇总，跟审批回写/管理员手动补卡是同一个道理——不然跨月夜班下班卡、
        // 设备断网补传等场景落在"月初自动生成汇总"之后时，月度汇总会停留在陈旧数字，要等下次人工点
        // "重新生成"或员工自己打开"我的日历"（只重算当月）才会更新；上月的汇总不会自己更新
        // （2026-09-21 代码审查发现）。只传这一个人，不会引发全库重算。
        await GenerateMonthlySummaryAsync(workDate.Year, workDate.Month, [userId]);

        // 把结果返回给网页显示
        return new PunchResponseDto
        {
            Success    = true,
            Message    = message,
            PunchTime  = punchTime,
            Status     = status,
            StatusText = StatusText(status),
            LateMinutes = lateMinutes
        };
    }

    /// <summary>
    /// 确定这次打卡应该归到哪一天的考勤记录（workDate），以及要续上的"昨天那条记录"（没有则为 null）。
    /// 详细口径见方法体里的注释；从 <see cref="PunchCoreAsync"/> 里原样搬出，逻辑没有改动。
    /// </summary>
    private async Task<(DateOnly WorkDate, AttendanceRecord? OpenYesterdayRecord)> ResolvePunchWorkDateAsync(
        int userId, PunchType punchType, DateOnly today, DateTime now)
    {
        // ── 确定这次打卡应该归到哪一天的考勤记录（workDate）──
        // 跨天（夜班）班次是"18:00 上班、次日凌晨下班"，打下班卡时日历已经翻到第二天了：
        // 不能直接拿"打卡这一刻的日历日期"去找/建记录，否则会把下班时间分裂成单独一条新记录，
        // 原来那条上班记录永远缺下班时间（会被判成"未打卡"），工时也永远算不出来。
        // 做法：下班卡（以及夜班过了午夜之后打的午间必打卡）优先续上"昨天已打上班卡、还没打下班卡"的记录——
        // 但只在昨天排的确实是跨天班次时才续，避免把员工真的忘记打卡的旧记录（普通白班）误接到今天的打卡上。
        // 午间打卡也要一起处理：不然夜班配的午间窗口如果落在凌晨（比如 02:00~03:00），
        // 这次打卡会被错误地记到"今天"这条本来就不该存在的新记录上，还会连带影响漏打卡窗口的判定。
        var workDate         = today;
        AttendanceRecord? openYesterdayRecord = null;
        if (punchType is PunchType.ClockOut or PunchType.MidCheck)
        {
            var yesterday = today.AddDays(-1);
            var candidate = await db.AttendanceRecords.FirstOrDefaultAsync(r =>
                r.UserId == userId && r.WorkDate == yesterday
                && r.ClockInTime != null && r.ClockOutTime == null);
            if (candidate is not null)
            {
                var yesterdayAssignment = await GetShiftAssignmentAsync(userId, yesterday);
                if (yesterdayAssignment?.ShiftSchedule is { IsCrossDay: true } ys && IsWithinNightCarryOver(yesterday, ys, now))
                {
                    workDate            = yesterday;
                    openYesterdayRecord = candidate;
                }
                // 白班/没排班的人加班过了零点才下班：这张下班卡也是昨天那条记录的（不能变成今天的上班卡）
                else if (punchType == PunchType.ClockOut
                         && IsPostMidnightClockOutOfDayShift(now, candidate.ClockInTime!.Value, yesterdayAssignment?.ShiftSchedule,
                                                             (await GetShiftAssignmentAsync(userId, today))?.ShiftSchedule))
                {
                    workDate            = yesterday;
                    openYesterdayRecord = candidate;
                }
            }
            else
            {
                // 昨天那条记录已经关闭（下班卡已经打过了）：同一次下班/午间打卡的重复提交如果跨了分钟
                // （开头的去重只挡同一分钟内的），会因为这里查不到"还开着"的候选记录而退化成今天的打卡，
                // 把昨天的下班时间错误地写进今天的新记录——跟考勤机同步那边"30 分钟内重复刷仍归昨天"用同一个
                // 口径兜底（2026-09-30 复核反馈：李杰那次事故如果两次请求刚好跨分钟，这里之前接不住）
                var closed = await db.AttendanceRecords.FirstOrDefaultAsync(r =>
                    r.UserId == userId && r.WorkDate == yesterday && r.ClockOutTime != null);
                // 不能只认"昨天是跨天班次"：白班/没排班的人加班过零点才下班（IsPostMidnightClockOutOfDayShift
                // 管的那种），下班卡本身就已经打在零点之后，几分钟内的重复刷卡同样要续到昨天，不然会把今天
                // 真正的迟到分钟数盖掉、工时多算（2026-09-30 复核发现：白班加班到 00:40，考勤机连刷两次，
                // 第二次被当成今天的上班卡，今天 09:10 真正到岗反而被记成午间卡）
                if (closed?.ClockOutTime is { } yOut && now >= yOut && now - yOut <= TimeSpan.FromMinutes(RepeatAfterClockOutMinutes)
                    && (DateOnly.FromDateTime(yOut) > yesterday
                        || (await GetShiftAssignmentAsync(userId, yesterday))?.ShiftSchedule is { IsCrossDay: true }))
                {
                    workDate            = yesterday;
                    openYesterdayRecord = closed;
                }
            }
        }
        // 上班卡：昨天排的是跨天夜班、但一次卡都没打（不是"打了上班卡还没下班"那种，那种是上面 ClockOut 分支管的），
        // 现在还在昨晚那班的续接宽限期内、且还没到今天自己班次可以打卡的时刻——这是很晚才想起来给昨晚那班打上班卡，
        // 应该算成昨天那班很晚的上班卡（记很晚的迟到），不能把它当成"今天全新的一天"
        // （那样会拿明天晚上才开始的班次去比对，误判成"打得太早"，2026-09-29 反馈）
        else if (punchType == PunchType.ClockIn)
        {
            var yesterday = today.AddDays(-1);
            var yesterdayAssignment = await GetShiftAssignmentAsync(userId, yesterday);
            if (yesterdayAssignment?.ShiftSchedule is { IsCrossDay: true } ys)
            {
                var yesterdayRecord = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == yesterday);
                var todayShiftForRecovery = (await GetShiftAssignmentAsync(userId, today))?.ShiftSchedule;
                if (IsVeryLateClockInForYesterdayShift(yesterday, ys, yesterdayRecord?.ClockInTime, now, todayShiftForRecovery))
                {
                    workDate            = yesterday;
                    openYesterdayRecord = yesterdayRecord;
                }
            }
        }
        return (workDate, openYesterdayRecord);
    }

    /// <summary>上班卡：按"取当天最早一次"更新上班时间、迟到状态和迟到分钟数；返回（状态, 迟到分钟, 提示语）。</summary>
    private static (AttendanceStatus Status, int? LateMinutes, string Message) ApplyClockIn(
        AttendanceRecord record, DateOnly workDate, DateTime punchTime, ShiftSchedule? shift, bool isRestDay)
    {
        string message;
        var    status      = AttendanceStatus.Normal;
        int?   lateMinutes = null;
        // 取当天最早一次上班打卡，跟考勤机同步（ZKDeviceSyncService）的口径保持一致，
        // 不是无条件覆盖成最新一次——不然重复提交/网络重试，有可能把一个准点的早期时间
        // 覆盖成偏晚的时间，凭空制造出"迟到"。
        if (record.ClockInTime is null || punchTime < record.ClockInTime)
        {
            record.ClockInTime = punchTime;
            status = CalcClockInStatus(workDate, punchTime, shift, isRestDay, out var lateMin);   // 算是否迟到
            // 只在当天状态还是由打卡本身决定的（正常/迟到/早退/未打卡/旷工）时才更新——
            // 旷工可以被真的打了上班卡这件事纠正回来（人确实来了），但请假/出差/节假日
            // 这些由审批流程或定时任务设置的状态，不能被一次上班打卡顺手覆盖掉
            // （比如批准了半天假、下午才打卡上班，不能把"请假"直接改成"正常"）。
            if (record.AttendanceStatus is AttendanceStatus.Normal or AttendanceStatus.Late
                or AttendanceStatus.EarlyLeave or AttendanceStatus.NotPunched or AttendanceStatus.Absent)
                record.AttendanceStatus = status;
            // 出差/节假日这两个状态当天不该有迟到分钟数——打卡流水本身照常记（审计用），
            // 但不写回 LateMinutes，避免报表里"迟到分钟"合计混进这两类日子的残留数字。
            // 请假（半天假）不再排除在外：下午才批准的半天假、上午准点/迟到上班，迟到分钟数
            // 该算就算，不能因为这天最终状态是"请假"就被抹掉（2026-09-17 支持半天请假后的口径）。
            if (record.AttendanceStatus is not (AttendanceStatus.BusinessTrip or AttendanceStatus.Holiday))
                record.LateMinutes  = lateMin;
            lateMinutes             = lateMin > 0 ? lateMin : null;
            message = lateMin > 0 ? $"上班打卡成功，迟到 {lateMin} 分钟" : "上班打卡成功";
        }
        else
        {
            // 已经有更早的上班记录了，这次重复打卡不改变结果
            status  = record.AttendanceStatus;
            message = "上班打卡成功";
        }
        return (status, lateMinutes, message);
    }

    /// <summary>下班卡：只在比已有下班时间更晚时更新，算早退，并在上下班卡齐了之后结算实际工时；返回（状态, 提示语）。</summary>
    private async Task<(AttendanceStatus Status, string Message)> ApplyClockOutAsync(
        AttendanceRecord record, User user, DateOnly workDate, DateTime punchTime, ShiftSchedule? shift, bool isRestDay)
    {
        string message;
        AttendanceStatus status;
        // 只有这次打卡时间比已经记录的下班时间更晚（或者还没有下班记录）才更新——避免本地/远程
        // 打卡的请求乱序到达时（比如设备同步已经记了更晚的下班时间，之后又收到一次时间更早的
        // 补传/重试请求），把已经记录的更晚的下班时间"倒退"回去，放大早退分钟数、少算工时。
        // 跟 ZKDeviceSyncService 的"取更晚"口径保持一致（发现于 2026-09-18 数据核查：这两条路径
        // 之前一个是无条件覆盖、一个是取更晚，口径不一样）。
        if (record.ClockOutTime is null || punchTime > record.ClockOutTime)
        {
            record.ClockOutTime = punchTime;
            status = CalcClockOutStatus(workDate, punchTime, shift, isRestDay, out var earlyMin);  // 算是否早退
            // 出差/节假日当天不该有早退分钟数残留，理由同上面的 LateMinutes；请假（半天假）同理不再排除
            if (record.AttendanceStatus is not (AttendanceStatus.BusinessTrip or AttendanceStatus.Holiday))
                record.EarlyLeaveMinutes = earlyMin;
            // 只在当天状态还是"正常/早退/未打卡"这种由打卡本身决定的状态时才更新——
            // "未打卡"要能被这次下班打卡覆盖掉（既然真的打了下班卡，就不再是"未打卡"了）；
            // 已经迟到的不会被这次的下班状态覆盖掉，请假/出差/节假日/旷工这些状态也不受影响。
            if (record.AttendanceStatus is AttendanceStatus.Normal or AttendanceStatus.EarlyLeave or AttendanceStatus.NotPunched)
                record.AttendanceStatus = status;
            if (earlyMin > 0)
            {
                message = $"下班打卡成功，早退 {earlyMin} 分钟";
            }
            else
            {
                message = "下班打卡成功";
            }

            // 上下班卡都齐了，算实际工时（不早于应上班时间、不晚于应下班时间——早到晚走都不多算钱）。
            // 加班不再从打卡时间估算：只认「加班申请」审批通过后累加的时长，这里不动 OvertimeHours。
            // 出差/节假日当天工时已经由审批流程/定时任务定好（标准工时/0），不能被这里顺手打的下班卡
            // 覆盖掉（打卡流水本身照常记，仅作审计）。请假不再整天排除在外：半天假当天如果还有真实
            // 打卡，按"标准工时 − 已批准的请假小时数"封顶结算，全天假（LeaveHours≥标准工时）上限
            // 自动变成 0，跟原来"请假当天工时恒为 0"的效果一致（2026-09-17 支持半天请假）。
            if (record.ClockInTime.HasValue
                && record.AttendanceStatus is not (AttendanceStatus.BusinessTrip or AttendanceStatus.Holiday))
            {
                var computedHours = await ComputeDailyWorkHoursAsync(
                    record, workDate, record.ClockInTime.Value, punchTime, shift, user.AttendanceGroupId);
                record.ActualWorkHours = record.AttendanceStatus == AttendanceStatus.OnLeave
                    ? ApplyLeaveHoursCap(computedHours, record.LeaveHours, ResolveDailyStandardHours(shift, appOptions.Value.DefaultDailyWorkHours))
                    : computedHours;
            }
        }
        else
        {
            // 已经有更晚的下班记录了，这次乱序/重复打卡不改变结果
            status  = record.AttendanceStatus;
            message = "下班打卡成功";
        }
        return (status, message);
    }

    /// <summary>手机 GPS 精度差的时候最多给多少米的容错——不能无限相信浏览器报上来的精度值
    /// （报的精度本身也可能不准，或者干脆被伪造），超过这个数就按这个数封顶，不能让"定位精度"
    /// 变成想让打卡通过多远都能通过的口子。</summary>
    private const double MaxLocationAccuracyToleranceMeters = 100;

    /// <summary>
    /// 校验一个经纬度是否落在指定考勤组配置的允许打卡地点范围内。没分配考勤组、或考勤组没开"定位打卡"、
    /// 或没配置任何地点，一律算不通过——远程打卡必须先定位、确认在允许的地点里，才能进入人脸识别
    /// （2026-09-29 用户确认：不能出现"考勤组没配定位，员工就能在任意地点远程打卡"这种口子）。
    /// 远程打卡会在真正调用（付费的）阿里云人脸识别接口之前，先调这个方法确认人在允许的地点里，
    /// 不在范围内就直接拒绝，不用白白浪费一次人脸识别调用。
    /// </summary>
    public async Task<(bool Valid, string? Message)> ValidateLocationAsync(int? attendanceGroupId, double? latitude, double? longitude, double? accuracyMeters = null)
    {
        if (!attendanceGroupId.HasValue)
            return (false, "您还未分配考勤组，暂不能使用远程打卡，请联系管理员");

        var group = await db.AttendanceGroups
            .Include(g => g.Locations)
            .FirstOrDefaultAsync(g => g.Id == attendanceGroupId.Value);
        if (group is not { EnableLocationPunch: true } || group.Locations.Count == 0)
            return (false, "您所在的考勤组尚未开启或配置定位打卡，暂不能使用远程打卡，请联系管理员先在考勤组设置里开启并配置打卡地点");

        if (!latitude.HasValue || !longitude.HasValue)
            return (false, "该考勤组已启用定位打卡，请允许浏览器获取位置权限后重试");

        // 算到每个配置地点的距离，取最近的一个来判断（多地点是"或"的关系，命中一个就算通过）
        var nearest = group.Locations
            .Select(l => new
            {
                l.LocationName,
                l.RadiusMeters,
                Distance = HaversineMeters(latitude.Value, longitude.Value, l.Latitude, l.Longitude)
            })
            .OrderBy(x => x.Distance)
            .First();

        if (nearest.Distance > nearest.RadiusMeters)
        {
            // 手机定位本身就是个"大概位置 ± 精度半径"的圆，人明明在范围里、但 GPS 飘了几十米导致
            // 算出来的点刚好落在圈外，这种情况不该被硬拒——只要把精度误差算进去后能够到范围内就放行。
            var tolerance = Math.Min(accuracyMeters ?? 0, MaxLocationAccuracyToleranceMeters);
            if (tolerance > 0 && nearest.Distance - tolerance <= nearest.RadiusMeters)
                return (true, null);

            var accuracyHint = accuracyMeters.HasValue ? $"，当前定位精度 ±{accuracyMeters:F0} 米" : "";
            return (false, $"打卡位置超出有效范围（距最近的「{nearest.LocationName ?? "打卡点"}」{nearest.Distance:F0} 米，限 {nearest.RadiusMeters} 米内{accuracyHint}）");
        }

        return (true, null);
    }

    /// <summary>
    /// 取某员工今天的考勤记录。
    /// 夜班跨天：如果今天还没有记录，但昨天有一条"已打上班卡、还没打下班卡"且排的是跨天班次的记录，
    /// 就返回昨天那条——否则半夜打开"我的打卡"页面会显示"今日暂无记录"，上班按钮又变回可点，
    /// 让人误以为之前打的上班卡凭空消失了（实际上打卡数据还在，只是查询没找对记录）。
    /// </summary>
    /// <summary>夜班下班卡最晚能晚到什么时候，还算"昨天那个夜班"的：昨天班次应下班时间之后这么多小时以内。
    /// 超过就当新一天的打卡，不再接到昨天那条没打下班卡的记录上（否则夜班漏打一次下班卡，
    /// 第二天晚上的上班卡会被当成昨天的下班卡，连环出错——2026-09-24 第 11 轮审查）。</summary>
    public const int NightShiftCarryOverHours = 6;

    /// <summary>跨天班次的上班卡最早能打到应上班时间前多少小时。更早的不当上班卡——比如 20:30 上班的晚班，
    /// 早上 08:40 打的卡（多半是下班后重复刷、或凌晨补刷）不能记成这天的上班卡，不然真正晚上来上班的那次会被当成午间卡，
    /// 整天上班卡丢失（2026-09-28 线上出现 13 条）。</summary>
    public const int CrossDayClockInEarlyHours = 6;

    /// <summary>夜班下班卡打完后多少分钟内，再打一次算"重复刷"，不生成新的上班卡。</summary>
    public const int RepeatAfterClockOutMinutes = 30;

    /// <summary>跨天班次的上班卡打得太早：离 workDate 当天应上班时间超过 <see cref="CrossDayClockInEarlyHours"/> 小时。</summary>
    public static bool IsTooEarlyForCrossDayClockIn(DateOnly workDate, DateTime punchTime, ShiftSchedule? shift) =>
        shift is { IsCrossDay: true }
        && punchTime < workDate.ToDateTime(shift.WorkStartTime).AddHours(-CrossDayClockInEarlyHours);

    /// <summary>这次上班卡，是不是"昨天那个还没打上班卡的跨天夜班"很晚才想起来打的：昨天排的是跨天班次、
    /// 一次卡都没打（不是"打了上班卡还没打下班卡"那种续接场景，那种走 <see cref="IsWithinNightCarryOver"/>），
    /// 且现在还在昨晚班次允许续接的时间窗内（跟 <see cref="IsWithinNightCarryOver"/> 用同一个
    /// <see cref="NightShiftCarryOverHours"/> 宽限期，而不是卡死在班次应下班时间那一刻）。是的话应该算成昨天那班
    /// 很晚的上班卡（记一次很晚的迟到），不能被"打得太早"的规则拦下——那条规则比对的是"今天晚上"的班次，对完全
    /// 没打卡、很晚才想起来打的人来说答非所问（2026-09-29 反馈：员工被提示"最早 14:30 起可以打上班卡"，其实他是
    /// 想给已经开始几小时的昨晚那班打卡）。宽限期跟"已经打了上班卡、只是还没打下班卡"这种记录能续接的时间窗
    /// （下班时间之后还有 6 小时宽限）统一，不再卡死在班次应下班时间那一刻（2026-09-30 反馈：晚班员工缺了上班卡，
    /// 后面的午间打卡和下班打卡都打不了）。
    /// ★ <paramref name="todayShift"/>：今天如果自己也排了班（继续上夜班、或轮换到别的班次），且这次打卡已经到了
    /// 今天班次自己"可以开始打上班卡"的时刻（今天上班时间往前 <see cref="CrossDayClockInEarlyHours"/> 小时），
    /// 就优先算今天的、不再追认成昨天的——不然昨天请了全天假/一次卡都没打时，今天正常的上班卡只要恰好落在
    /// 延长后的 6 小时宽限窗口里，就会被错误地记到昨天的请假记录上（2026-09-30 复核反馈：一次弄乱两天的记录，
    /// 迟到分钟数还会算出几百甚至上千的离谱数字，进报表）。
    /// ★ 这个参数没有默认值：调用方必须显式传"今天排的是什么班次"（没排班传 null），不能让编译器悄悄放过
    /// 一个漏传的调用点，退回到没考虑"今天"这一侧的旧判断（2026-09-30 复核建议）。</summary>
    public static bool IsVeryLateClockInForYesterdayShift(DateOnly yesterday, ShiftSchedule? yesterdayShift, DateTime? yesterdayClockIn, DateTime punchTime, ShiftSchedule? todayShift)
    {
        if (yesterdayShift is not { IsCrossDay: true } || yesterdayClockIn is not null) return false;
        if (todayShift is not null
            && punchTime >= yesterday.AddDays(1).ToDateTime(todayShift.WorkStartTime).AddHours(-CrossDayClockInEarlyHours))
            return false;
        return punchTime <= yesterday.ToDateTime(yesterdayShift.WorkEndTime).AddDays(1).AddHours(NightShiftCarryOverHours);
    }

    /// <inheritdoc />
    public async Task<string?> GetClockInRejectionAsync(int userId, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        // 今天已经有上班卡了，不是"新的上班卡"，不归这里管
        if (await db.AttendanceRecords.AnyAsync(r => r.UserId == userId && r.WorkDate == today && r.ClockInTime != null))
            return null;

        var yesterday = today.AddDays(-1);

        // ① 刚打完下班卡又点了一次：昨天那条记录已经有下班卡、且就在刚刚——不能只认"昨天是跨天班次"，
        // 白班/没排班的人加班过零点才下班（下班卡本身已经打在零点之后）同样要拦，理由跟下面
        // PunchCoreAsync 的同名兜底一致
        var yOut = await db.AttendanceRecords
            .Where(r => r.UserId == userId && r.WorkDate == yesterday && r.ClockOutTime != null)
            .Select(r => r.ClockOutTime).FirstOrDefaultAsync();
        if (yOut is { } outTime && now >= outTime && now - outTime <= TimeSpan.FromMinutes(RepeatAfterClockOutMinutes)
            && (DateOnly.FromDateTime(outTime) > yesterday
                || (await GetShiftAssignmentAsync(userId, yesterday))?.ShiftSchedule is { IsCrossDay: true }))
            return $"您刚刚（{outTime:HH:mm}）已经打过下班卡，无需重复打卡";

        // todayShift 提前到这里查：①.5 判断"该不该追认成昨天很晚的上班卡"时，也要知道今天自己是不是也排了班、
        // 这次打卡是不是已经到了今天班次自己可以打卡的时刻，不然昨天请了全天假/一次卡都没打时，今天正常的
        // 上班卡只要落在延长后的宽限窗口里就会被错误地记到昨天（2026-09-30 复核反馈）
        var todayShift = (await GetShiftAssignmentAsync(userId, today))?.ShiftSchedule;

        // ①.5 昨晚那班一次卡都没打、现在还在续接宽限期内、且还没到今天自己班次可以打卡的时刻：
        // 当成很晚的上班卡放行，不当"打得太早"
        var yesterdayAssignment = await GetShiftAssignmentAsync(userId, yesterday);
        if (yesterdayAssignment?.ShiftSchedule is { IsCrossDay: true } ys)
        {
            var yesterdayClockIn = await db.AttendanceRecords
                .Where(r => r.UserId == userId && r.WorkDate == yesterday)
                .Select(r => r.ClockInTime).FirstOrDefaultAsync();
            if (IsVeryLateClockInForYesterdayShift(yesterday, ys, yesterdayClockIn, now, todayShift))
                return null;
        }

        // ② 跨天班次的上班卡不能打得离应上班时间太早
        if (IsTooEarlyForCrossDayClockIn(today, now, todayShift))
        {
            var earliest = today.ToDateTime(todayShift!.WorkStartTime).AddHours(-CrossDayClockInEarlyHours);
            return $"现在不在您班次（{todayShift.WorkStartTime:HH\\:mm} 上班）的上班打卡时间内，最早 {earliest:HH\\:mm} 起可以打上班卡。如果是漏打了上班卡，请提交补卡申请";
        }
        return null;
    }

    /// <summary>非跨天班次（白班/中班/没排班）的人加班过了零点才下班：零点后的这张卡是"昨天那条没打下班卡的记录"的下班卡，
    /// 不是今天的上班卡。判断：昨天有上班卡；这次打卡离昨天上班卡不超过 <see cref="PostMidnightClockOutMaxHours"/> 小时；
    /// 而且比今天应上班时间早了 <see cref="CrossDayClockInEarlyHours"/> 小时以上（没排班就当 06:00 之前）——
    /// 这么早不可能是今天的上班卡。昨天排的是跨天班次的走原来的夜班续接（<see cref="IsWithinNightCarryOver"/>），不在这里处理
    /// （2026-09-28 全项目审查：白班加班到 00:40 下班，当天工时被清零、23:55 还被标未打卡，第二天早上的上班卡又被当成午间卡）。
    /// ★ 要求昨天或今天至少有一天排了班（哪怕不是跨天班次）：完全没排班的人没有"班次"这个参照，没法判断
    /// 这算不算合理的加班延续，径直按最长 <see cref="PostMidnightClockOutMaxHours"/> 小时合并，会把两个本来
    /// 无关的独立工作时段错拼成一个 18 小时以上的"班"（2026-09-29 审查发现 H1：没排班的人昨天 10:00 上班忘打
    /// 下班卡，第二天 05:30 到岗打卡，被当成昨天的下班卡算出 18 小时）。</summary>
    public static bool IsPostMidnightClockOutOfDayShift(DateTime punchTime, DateTime yesterdayClockIn, ShiftSchedule? yesterdayShift, ShiftSchedule? todayShift)
    {
        if (yesterdayShift is { IsCrossDay: true }) return false;
        if (yesterdayShift is null && todayShift is null) return false;
        if (punchTime <= yesterdayClockIn || punchTime - yesterdayClockIn > TimeSpan.FromHours(PostMidnightClockOutMaxHours)) return false;
        var day = DateOnly.FromDateTime(punchTime);
        var cutoff = todayShift is null
            ? day.ToDateTime(new TimeOnly(6, 0))
            : day.ToDateTime(todayShift.WorkStartTime).AddHours(-CrossDayClockInEarlyHours);
        return punchTime < cutoff;
    }

    /// <summary>白班加班过零点下班：这次打卡最晚离昨天上班卡多少小时以内，才当成昨天的下班卡。</summary>
    public const int PostMidnightClockOutMaxHours = 20;

    /// <summary>补卡申请里的"下班卡"该落在哪一天：默认是申请的日期。已有上班卡、且这个时间点不晚于上班卡时，
    /// 只有看起来像是跨天班次的下班（班次本身跨天，或者填的时间在凌晨——真的很像"第二天早上几点下班"）才
    /// 顺延到第二天；否则保留在当天，让下班早于/等于上班这个矛盾按原有的"时间异常"规则被标出来，等人工核实。
    /// 没有上班卡时，班次是跨天班次且填的时间早于班次上班时间，也当成第二天。
    /// ★ 不能任何"填反了"的时间都无条件顺延：普通白班/没排班的人补卡把 18:00 手滑填成 08:00（比上班时间还早）
    /// 不像夜班，顺延成第二天会算出一个 22 小时的班，反而把明显的数据错误悄悄放过去了
    /// （2026-09-29 审查发现 H1，以前这种情况会被标"时间异常"提醒人工核实，不该被这次改动误伤）。</summary>
    public static DateTime ResolvePunchReplenishmentClockOut(DateOnly date, TimeOnly time, DateTime? clockIn, ShiftSchedule? shift)
    {
        var dt = date.ToDateTime(time);
        if (clockIn is { } ci && dt <= ci)
        {
            var looksLikeCrossDay = shift is { IsCrossDay: true } || time < new TimeOnly(6, 0);
            return looksLikeCrossDay ? dt.AddDays(1) : dt;
        }
        if (shift is { IsCrossDay: true } && time < shift.WorkStartTime) return dt.AddDays(1);
        return dt;
    }

    /// <summary>这次打卡时间，是否还在"昨天那个跨天夜班"允许续接的时间窗内。</summary>
    public static bool IsWithinNightCarryOver(DateOnly shiftDate, ShiftSchedule shift, DateTime punchTime) =>
        punchTime <= shiftDate.ToDateTime(shift.WorkEndTime).AddDays(1).AddHours(NightShiftCarryOverHours);
}
