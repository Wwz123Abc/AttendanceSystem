using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 考勤核心服务：打卡、考勤记录查询、月度汇总等。
/// 打卡时会根据员工的考勤组/班次，实时算出迟到、早退、加班、实际工时和考勤状态。
/// </summary>
public class AttendanceService(AttendanceDbContext db, IOptions<AppSettingsOptions> appOptions, ILogger<AttendanceService> logger) : IAttendanceService
{
    private const int MaxPunchAttempts = 5;

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
        throw new InvalidOperationException("打卡失败，请重试");
    }

    private async Task<PunchResponseDto> PunchCoreAsync(int userId, PunchRequestDto request, bool skipLocationCheck)
    {
        var now   = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        var user = await db.Users.FindAsync(userId)
            ?? throw new KeyNotFoundException("用户不存在");

        // 没办入职（没填入职日期或还没到入职日）不让打卡，避免产生异常数据
        if (user.HireDate is null || user.HireDate.Value > today)
            return new PunchResponseDto { Success = false, Message = "您尚未办理入职（入职日期未设置或未到），暂不能打卡，请联系管理员" };

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
        if (request.PunchType is PunchType.ClockOut or PunchType.MidCheck)
        {
            var yesterday = today.AddDays(-1);
            var candidate = await db.AttendanceRecords.FirstOrDefaultAsync(r =>
                r.UserId == userId && r.WorkDate == yesterday
                && r.ClockInTime != null && r.ClockOutTime == null);
            if (candidate is not null)
            {
                var yesterdayAssignment = await GetShiftAssignmentAsync(userId, yesterday);
                if (yesterdayAssignment?.ShiftSchedule.IsCrossDay == true)
                {
                    workDate            = yesterday;
                    openYesterdayRecord = candidate;
                }
            }
        }

        // 节假日不用打卡（按 workDate 判断，而不是打卡当下的日历日期——
        // 否则夜班下班卡如果跨到了假期第一天，会被误判成"今日为节假日"而拒绝下班打卡）
        if (await IsHolidayAsync(workDate, user.AttendanceGroupId))
            return new PunchResponseDto { Success = false, Message = "今日为节假日，无需打卡" };

        // 定位打卡校验：考勤组开了定位打卡、且配了打卡地点才会真的比对距离，没开/没配就直接放行。
        // skipLocationCheck=true（远程打卡）时整段跳过——远程打卡自己会在调用这个方法之前先调
        // ValidateLocationAsync 做过一次同样的校验了（见该方法注释），这里不用再查一遍数据库重复判断。
        if (!skipLocationCheck)
        {
            var (locationValid, locationMessage) =
                await ValidateLocationAsync(user.AttendanceGroupId, request.Latitude, request.Longitude, request.Accuracy);
            if (!locationValid)
                return new PunchResponseDto { Success = false, Message = locationMessage! };
        }

        // 打卡时间精确到分钟（把秒抹掉）
        var punchTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);

        // 取 workDate 那天的排班，进而拿到班次（用来判断迟到/早退/加班）
        var assignment = await GetShiftAssignmentAsync(userId, workDate);
        var shift      = assignment is not null
            ? await db.ShiftSchedules.FindAsync(assignment.ShiftScheduleId)
            : null;
        // 今天是不是这个员工的休息日：休息日没有"应上班/应下班时间"可比，不该判迟到/早退
        // （调班补班日不算休息日，仍按班次时间正常判断）
        var isRestDay = await IsNonCompRestDayAsync(db, workDate, shift, user.AttendanceGroupId);

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
        var    status      = AttendanceStatus.Normal;
        int?   lateMinutes = null;

        if (request.PunchType == PunchType.ClockIn)   // ── 上班卡 ──
        {
            // 取当天最早一次上班打卡，跟考勤机同步（ZKDeviceSyncService）的口径保持一致，
            // 不是无条件覆盖成最新一次——不然重复提交/网络重试，有可能把一个准点的早期时间
            // 覆盖成偏晚的时间，凭空制造出"迟到"。
            if (record.ClockInTime is null || punchTime < record.ClockInTime)
            {
                record.ClockInTime = punchTime;
                status = CalcClockInStatus(punchTime, shift, isRestDay, out var lateMin);   // 算是否迟到
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
        }
        else if (request.PunchType == PunchType.MidCheck)   // ── 午间打卡 ──
        {
            // 打卡流水已经在上面写好了；这里不改上下班时间/状态，工时结算在下班打卡时统一算（见 ResolveEffectiveClockInAsync）
            status  = record.AttendanceStatus;
            message = "午间打卡成功";
        }
        else                                          // ── 下班卡 ──
        {
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
        }

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

    /// <summary>手机 GPS 精度差的时候最多给多少米的容错——不能无限相信浏览器报上来的精度值
    /// （报的精度本身也可能不准，或者干脆被伪造），超过这个数就按这个数封顶，不能让"定位精度"
    /// 变成想让打卡通过多远都能通过的口子。</summary>
    private const double MaxLocationAccuracyToleranceMeters = 100;

    /// <summary>
    /// 校验一个经纬度是否落在指定考勤组配置的允许打卡地点范围内。考勤组没开"定位打卡"、或没配置任何
    /// 地点，直接算通过（跟 PunchAsync 里原来的定位校验是同一套判断，抽出来给远程打卡单独调用）。
    /// 远程打卡会在真正调用（付费的）阿里云人脸识别接口之前，先调这个方法确认人在允许的地点里，
    /// 不在范围内就直接拒绝，不用白白浪费一次人脸识别调用。
    /// </summary>
    public async Task<(bool Valid, string? Message)> ValidateLocationAsync(int? attendanceGroupId, double? latitude, double? longitude, double? accuracyMeters = null)
    {
        if (!attendanceGroupId.HasValue) return (true, null);   // 没分配考勤组，没有地点可比对，不限制

        var group = await db.AttendanceGroups
            .Include(g => g.Locations)
            .FirstOrDefaultAsync(g => g.Id == attendanceGroupId.Value);
        if (group is not { EnableLocationPunch: true } || group.Locations.Count == 0)
            return (true, null);   // 没开定位打卡/没配置地点，不限制

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
    public async Task<AttendanceRecordDto?> GetTodayAttendanceAsync(int userId)
    {
        var today  = DateOnly.FromDateTime(DateTime.Today);
        var record = await db.AttendanceRecords
            .Include(r => r.User)
            .ThenInclude(u => u.Department)
            .FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == today);

        if (record is null)
        {
            var yesterday = today.AddDays(-1);
            var openYesterday = await db.AttendanceRecords
                .Include(r => r.User).ThenInclude(u => u.Department)
                .FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == yesterday
                                       && r.ClockInTime != null && r.ClockOutTime == null);
            if (openYesterday is not null)
            {
                var yesterdayAssignment = await GetShiftAssignmentAsync(userId, yesterday);
                if (yesterdayAssignment?.ShiftSchedule.IsCrossDay == true)
                    record = openYesterday;
            }
        }

        return record is null ? null : ToDto(record);
    }

    /// <summary>查某员工的考勤记录列表（可按日期段或年月过滤）。</summary>
    public async Task<List<AttendanceRecordDto>> GetPersonalAttendanceAsync(PersonalAttendanceQueryDto q)
    {
        var query = db.AttendanceRecords
            .Include(r => r.User)
            .Where(r => r.UserId == q.UserId)
            .AsQueryable();

        if (q.StartDate.HasValue) query = query.Where(r => r.WorkDate >= q.StartDate.Value);
        if (q.EndDate.HasValue)   query = query.Where(r => r.WorkDate <= q.EndDate.Value);
        if (q.Year.HasValue && q.Month.HasValue)
            query = query.Where(r => r.WorkDate.Year == q.Year && r.WorkDate.Month == q.Month);

        return (await query.OrderByDescending(r => r.WorkDate).ToListAsync())
            .Select(ToDto).ToList();
    }

    /// <summary>查某部门/考勤组在一段时间内的所有人考勤记录。</summary>
    public async Task<List<AttendanceRecordDto>> GetDeptAttendanceAsync(DeptAttendanceQueryDto q, HashSet<int>? deptIds = null)
    {
        var userIds = await BuildUserIdQueryAsync(q.DepartmentId, q.AttendanceGroupId, deptIds);   // 先圈出这批人

        return (await db.AttendanceRecords
            .Include(r => r.User).ThenInclude(u => u.Department)
            .Where(r => userIds.Contains(r.UserId)
                     && r.WorkDate >= q.StartDate
                     && r.WorkDate <= q.EndDate)
            .OrderBy(r => r.WorkDate).ThenBy(r => r.User.EmployeeNo)
            .ToListAsync())
            .Select(ToDto).ToList();
    }

    /// <summary>取某员工某月的汇总（含每日明细）。</summary>
    public async Task<MonthlySummaryDto?> GetMonthlySummaryAsync(int userId, int year, int month)
    {
        var summary = await db.MonthlyAttendanceSummaries
            .Include(s => s.User).ThenInclude(u => u.Department)
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Year == year && s.Month == month);
        if (summary is null) return null;

        // 这个月的第一天到最后一天
        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);
        var daily = await db.AttendanceRecords
            .Include(r => r.User)
            .Where(r => r.UserId == userId && r.WorkDate >= start && r.WorkDate <= end)
            .OrderBy(r => r.WorkDate).ToListAsync();

        var dto = MapSummary(summary, daily.Select(ToDto).ToList());
        var night = await ComputeNightShiftDaysAsync([userId], year, month);
        dto.NightShiftDays = night.GetValueOrDefault(userId);
        return dto;
    }

    /// <summary>取某员工某月的排班安排（“我的排班”页用），按日期排序。</summary>
    public async Task<List<MyScheduleDto>> GetMyScheduleAsync(int userId, int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);

        return (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => a.UserId == userId && a.WorkDate >= start && a.WorkDate <= end)
                .OrderBy(a => a.WorkDate)
                .ToListAsync())
            .Select(a => new MyScheduleDto
            {
                WorkDate       = a.WorkDate,
                ShiftName      = a.ShiftSchedule.ShiftName,
                ShiftColor     = a.ShiftSchedule.Color,
                WorkStartText  = a.ShiftSchedule.WorkStartTime.ToString("HH:mm"),
                WorkEndText    = a.ShiftSchedule.WorkEndTime.ToString("HH:mm"),
                IsCrossDay     = a.ShiftSchedule.IsCrossDay,
                IsAutoAssigned = a.IsAutoAssigned
            })
            .ToList();
    }

    /// <summary>
    /// 取某部门/考勤组某月的汇总列表（不含每日明细）。
    /// 这里故意不按"当前是否在职"过滤——月度报表是历史记录，员工哪怕后来离职/停用了，
    /// 只要那个月确实生成过汇总，也应该继续能查到，不然离职员工那个月的数据会从报表里凭空消失。
    /// 部门/考勤组这两个字段员工离职后仍然保留（停用不会清空），所以按它们筛选不受影响。
    /// </summary>
    public async Task<List<MonthlySummaryDto>> GetDeptMonthlySummariesAsync(
        int? deptId, int? groupId, int year, int month, HashSet<int>? scopeDeptIds = null)
    {
        var idQuery = db.Users.AsQueryable();
        if (deptId.HasValue)  idQuery = idQuery.Where(u => u.DepartmentId == deptId.Value);
        if (groupId.HasValue) idQuery = idQuery.Where(u => u.AttendanceGroupId == groupId.Value);
        if (scopeDeptIds is not null) idQuery = idQuery.Where(u => u.DepartmentId != null && scopeDeptIds.Contains(u.DepartmentId.Value));
        var userIds = await idQuery.Select(u => u.Id).ToListAsync();

        var dtos = (await db.MonthlyAttendanceSummaries
            .Include(s => s.User).ThenInclude(u => u.Department)
            .Where(s => userIds.Contains(s.UserId) && s.Year == year && s.Month == month)
            .OrderBy(s => s.User.Department!.DeptName).ThenBy(s => s.User.EmployeeNo)
            .ToListAsync())
            .Select(s => MapSummary(s, [])).ToList();

        // 夜班天数不单独存表，这里按排班/打卡时间批量算出来回填
        var night = await ComputeNightShiftDaysAsync(dtos.Select(d => d.UserId).ToList(), year, month);
        foreach (var d in dtos) d.NightShiftDays = night.GetValueOrDefault(d.UserId);
        return dtos;
    }

    /// <summary>
    /// 生成"模板月度汇总表"：统计周期是调用方传入的任意起止日期（比如"上月26号至本月25号"这种薪资结算周期），
    /// 不依赖 MonthlyAttendanceSummary 这张按自然月生成的汇总表，而是直接从每日考勤记录/排班/假期现算，
    /// 这样才能支持不是自然月的统计区间。可选按部门 / 按考勤组自动带出的所属公司筛选。
    /// </summary>
    public async Task<TemplateReportResultDto> GenerateTemplateReportAsync(DateOnly start, DateOnly end, List<int>? deptIds)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var dates = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1)) dates.Add(d);
        var defaultDailyHours = appOptions.Value.DefaultDailyWorkHours;

        // 范围 = 现在在职的人 ∪ 这段周期里有考勤记录的人——这份表是给发工资用的，
        // 如果只按"现在是否在职"筛选，员工在这个薪资周期里离职、导出报表时已经被停用，
        // 就会整个人从表里消失，那个月的工资就算不出来了，所以必须把"曾经有过记录的人"也纳进来。
        var relevantIds = await db.Users.Where(u => u.IsActive).Select(u => u.Id)
            .Union(db.AttendanceRecords.Where(r => r.WorkDate >= start && r.WorkDate <= end).Select(r => r.UserId))
            .Distinct()
            .ToListAsync();

        var q = db.Users
            .Include(u => u.Department)
            .Include(u => u.AttendanceGroup)
            .Where(u => relevantIds.Contains(u.Id))
            .AsQueryable();
        // deptIds 是页面那棵"公司/部门"合并树里勾选出来的部门编号（勾大范围=公司节点，会连带展开成它底下所有部门的编号）；
        // 不勾任何部门 = 不筛选，导出全公司所有人。
        if (deptIds is { Count: > 0 }) q = q.Where(u => u.DepartmentId.HasValue && deptIds.Contains(u.DepartmentId.Value));
        var users = await q.OrderBy(u => u.Department!.DeptName).ThenBy(u => u.EmployeeNo).ToListAsync();
        var userIds = users.Select(u => u.Id).ToList();

        // 批量预取这段时间的考勤记录/排班/假期，循环里直接从内存取，避免每人都查一次库
        var recordsByUser = (await db.AttendanceRecords
                .Where(r => userIds.Contains(r.UserId) && r.WorkDate >= start && r.WorkDate <= end)
                .ToListAsync())
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.WorkDate));

        var assignByUser = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= start && a.WorkDate <= end)
                .ToListAsync())
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.WorkDate));

        var groupIds = users.Where(u => u.AttendanceGroupId.HasValue).Select(u => u.AttendanceGroupId!.Value).Distinct().ToList();
        var holidays = await db.Holidays
            .Where(h => h.HolidayDate >= start && h.HolidayDate <= end
                     && (h.AttendanceGroupId == null || groupIds.Contains(h.AttendanceGroupId.Value)))
            .ToListAsync();

        var night = await ComputeNightShiftDaysRangeAsync(userIds, start, end);

        var rows = new List<TemplateReportRowDto>();
        foreach (var user in users)
        {
            var recByDate    = recordsByUser.GetValueOrDefault(user.Id) ?? new Dictionary<DateOnly, AttendanceRecord>();
            var assignByDate = assignByUser.GetValueOrDefault(user.Id) ?? new Dictionary<DateOnly, ShiftAssignment>();
            var myHolidays   = holidays.Where(h => h.AttendanceGroupId == null || h.AttendanceGroupId == user.AttendanceGroupId).ToList();

            var row = new TemplateReportRowDto
            {
                UserId          = user.Id,
                RealName        = user.RealName,
                GroupName       = user.AttendanceGroup?.GroupName,
                DeptName        = user.Department?.DeptName,
                EmployeeNo      = user.EmployeeNo,
                Position        = user.Position,
                ContractCompany = user.ContractCompany,
                NightShiftDays  = night.GetValueOrDefault(user.Id)
            };

            // 标准工时：取这段时间里出现次数最多的那个班次的标准工时（没排过班就留空）
            row.StandardDailyHours = assignByDate.Values
                .GroupBy(a => a.ShiftScheduleId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.First().ShiftSchedule.StandardWorkHours)
                .FirstOrDefault();
            if (assignByDate.Count == 0) row.StandardDailyHours = null;

            decimal totalWork = 0, businessTripHours = 0, nightShiftHours = 0;
            decimal totalOtHours = 0, weekdayOtHours = 0, restOtHours = 0, holidayOtHours = 0;
            decimal actualDays = 0, leaveDays = 0;
            int restDays = 0, absentDays = 0, missingIn = 0, missingOut = 0;
            int lateMin = 0, earlyMin = 0, lateCnt = 0, earlyCnt = 0;

            foreach (var date in dates)
            {
                // 入职日之前的日子不算这个人的考勤范围——没有这一条，新员工入职前那几天会因为
                // "当天没有考勤记录、又不是休息日"被误判成旷工，把刚入职的人旷工天数算多
                if (user.HireDate is { } hireDate && date < hireDate)
                {
                    row.DailyHours.Add(null);
                    row.DailyIsNightShift.Add(false);
                    continue;
                }

                recByDate.TryGetValue(date, out var rec);
                assignByDate.TryGetValue(date, out var assign);
                var holiday      = myHolidays.FirstOrDefault(h => h.HolidayDate == date);
                var isCompDay    = holiday?.HolidayType == HolidayType.CompensatoryWorkDay;
                var isHolidayOff = holiday is not null && !isCompDay;                                              // 法定节假日/公司休息（非补班）
                var isShiftRest  = !isCompDay && IsShiftWeeklyRestDay(date, assign?.ShiftSchedule);  // 排的班自己配置的每周休息日，或没排班时按周末兜底

                // 当天是不是上的夜班：排班里配的是跨天班次/名字带"夜"就算；没排班时按打卡时间兜底
                // （18 点后上班，或下班跨到了第二天），口径和 ComputeNightShiftDaysRangeAsync 保持一致。
                var isNightShift = assign is not null && (assign.ShiftSchedule.IsCrossDay || assign.ShiftSchedule.ShiftName.Contains('夜'));
                if (!isNightShift && rec?.ClockInTime is { } nci)
                {
                    if (nci.Hour >= 18) isNightShift = true;
                    else if (rec.ClockOutTime is { } nco && nco.Date > nci.Date) isNightShift = true;
                }
                row.DailyIsNightShift.Add(isNightShift);

                decimal? dayHours = null;
                if (rec is not null)
                {
                    if (rec.ActualWorkHours > 0)
                    {
                        dayHours = FloorToHalf(rec.ActualWorkHours);       // 每日格子和月度合计统一按"半小时"取整口径
                        totalWork += FloorToHalf(rec.ActualWorkHours);     // 合计按"半小时"为最小单位累加，保证总数只会是整数或 x.5
                        if (isNightShift) nightShiftHours += FloorToHalf(rec.ActualWorkHours);   // 夜班总工时：当天算夜班才计入，取整口径和总工时一致
                    }
                    // 出勤天数/请假天数跟 GenerateMonthlySummaryAsync 共用同一个公式（ResolveAttendanceDayCredit），
                    // 不能只看"有没有工时"——半天假当天可能 ActualWorkHours>0（上午上班），整天假是 0，
                    // 两种都要正确记到"出勤"和"请假"里，不能像以前那样只用 ActualWorkHours>0 判断出勤、
                    // 完全没有"请假天数"这个概念，导致这份发工资用的报表和月度汇总页对不上
                    // （发现于 2026-09-18 数据核查）。
                    var dailyStdHours = assign?.ShiftSchedule.StandardWorkHours ?? defaultDailyHours;
                    actualDays += ResolveAttendanceDayCredit(rec, dailyStdHours);
                    if (rec.AttendanceStatus == AttendanceStatus.OnLeave)
                        leaveDays += ResolveLeaveDaysFraction(rec.LeaveHours, dailyStdHours);
                    if (rec.AttendanceStatus == AttendanceStatus.BusinessTrip) businessTripHours += FloorToHalf(rec.ActualWorkHours);
                    if (rec.AttendanceStatus == AttendanceStatus.Absent) absentDays++;
                    if (rec.ClockInTime is null && rec.ClockOutTime is not null) missingIn++;    // 有下班卡没上班卡
                    if (rec.ClockInTime is not null && rec.ClockOutTime is null) missingOut++;   // 有上班卡没下班卡
                    lateMin  += rec.LateMinutes;
                    earlyMin += rec.EarlyLeaveMinutes;
                    if (rec.LateMinutes > 0) lateCnt++;
                    if (rec.EarlyLeaveMinutes > 0) earlyCnt++;

                    if (rec.OvertimeHours > 0)
                    {
                        // 参考模板里"加班总时长"这几列的单位是小时，跟"工作时长"同一个口径，不用再换算。
                        // 每天的加班先按半小时取整再累加（而不是最后对合计取整）：这样"工作日+休息日+节假日"
                        // 三个分项加起来一定正好等于"加班总时长"，不会因为各自取整出现对不上的尾差。
                        var ot = FloorToHalf(rec.OvertimeHours);
                        totalOtHours += ot;
                        if (isHolidayOff) holidayOtHours += ot;
                        else if (isShiftRest) restOtHours += ot;
                        else weekdayOtHours += ot;
                    }
                }
                else if (!isHolidayOff && !isShiftRest && date <= today)
                {
                    // 完全没有考勤记录，又不是休息/节假日 → 算旷工（和后台旷工判定的口径一致）。
                    // 必须加 date <= today 这个限制：这份报表的统计周期是"上月26号至本月25号"，
                    // 只要在 25 号之前导出，周期里就会包含"还没到的未来几天"——这些天当然不会有考勤记录，
                    // 但它们不是旷工，是"还没发生"，不加这个判断会把每个人都算出一堆莫名其妙的旷工天数。
                    absentDays++;
                }

                if (isHolidayOff || isShiftRest) restDays++;

                row.DailyHours.Add(dayHours);
            }

            // 各项工时在上面累加时就已经按"半小时"取整过了，这里直接赋值（合计只会是整数或 x.5）
            row.ActualWorkdays          = actualDays;
            row.LeaveDays               = leaveDays;
            row.RestDays                = restDays;
            row.RegularWorkHours        = totalWork;              // 正班工时：不含加班，口径不变
            row.TotalWorkHours          = totalWork + totalOtHours;  // 工作时长合计：正班 + 加班
            row.LateMinutes             = lateMin;
            row.EarlyLeaveMinutes       = earlyMin;
            row.LateCount               = lateCnt;
            row.EarlyLeaveCount         = earlyCnt;
            row.MissingClockInCount     = missingIn;
            row.MissingClockOutCount    = missingOut;
            row.AbsentDays              = absentDays;
            row.BusinessTripHours       = businessTripHours;
            row.NightShiftHours         = nightShiftHours;
            row.TotalOvertimeHours      = totalOtHours;
            row.WeekdayOvertimeHours    = weekdayOtHours;
            row.RestDayOvertimeHours    = restOtHours;
            row.HolidayOvertimeHours    = holidayOtHours;

            rows.Add(row);
        }

        return new TemplateReportResultDto { StartDate = start, EndDate = end, Dates = dates, Rows = rows };
    }

    /// <summary>取"打卡时间表"导出用的数据，人员范围口径和 <see cref="GenerateTemplateReportAsync"/> 保持一致。</summary>
    public async Task<List<AttendanceRecordDto>> GetClockTimeSheetAsync(DateOnly start, DateOnly end, List<int>? deptIds)
    {
        var relevantIds = await db.Users.Where(u => u.IsActive).Select(u => u.Id)
            .Union(db.AttendanceRecords.Where(r => r.WorkDate >= start && r.WorkDate <= end).Select(r => r.UserId))
            .Distinct()
            .ToListAsync();

        var uq = db.Users.Where(u => relevantIds.Contains(u.Id)).AsQueryable();
        if (deptIds is { Count: > 0 }) uq = uq.Where(u => u.DepartmentId.HasValue && deptIds.Contains(u.DepartmentId.Value));
        var userIds = await uq.Select(u => u.Id).ToListAsync();

        return (await db.AttendanceRecords
                .Include(r => r.User).ThenInclude(u => u.Department)
                .Where(r => userIds.Contains(r.UserId) && r.WorkDate >= start && r.WorkDate <= end)
                .OrderBy(r => r.User.EmployeeNo).ThenBy(r => r.WorkDate)
                .ToListAsync())
            .Select(ToDto).ToList();
    }

    /// <summary>
    /// 生成/重算某月的考勤汇总（已存在就更新，没有就新建）。
    /// 处理范围不能只看"现在是否在职"：如果一个人这个月工作过、后来才离职（停用），
    /// 那他这个月的汇总必须照常算出来/保持更新，不能因为人已经离职就让这个月的历史记录消失
    /// ——不然离职员工最后一个月的考勤/工时在报表里会直接对不上账，这是财务和 HR 都要用的数据。
    /// 所以处理范围 = 现在在职的人 ∪ 这个月有考勤记录的人 ∪ 这个月已经生成过汇总的人。
    /// <paramref name="onlyUserId"/> 不为空时只重算这一个人（补卡/请假/加班/出差审批回写、
    /// 管理员手动补卡之后调用这个，避免月初已经生成过的汇总因为后补的记录而跟日明细对不上、
    /// 又得靠人工去点"重新生成"才能刷新）。
    /// </summary>
    public async Task GenerateMonthlySummaryAsync(int year, int month, IReadOnlyCollection<int>? onlyUserIds = null)
    {
        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);

        var candidateIds = onlyUserIds is { Count: > 0 }
            ? onlyUserIds.Distinct().ToList()
            : await db.Users.Where(u => u.IsActive).Select(u => u.Id)
                .Union(db.AttendanceRecords.Where(r => r.WorkDate >= start && r.WorkDate <= end).Select(r => r.UserId))
                .Union(db.MonthlyAttendanceSummaries.Where(s => s.Year == year && s.Month == month).Select(s => s.UserId))
                .Distinct()
                .ToListAsync();
        var users = await db.Users.Where(u => candidateIds.Contains(u.Id)).ToListAsync();

        // 本月每个人“审批通过”的申请数（一次性批量查，避免循环里逐人查库）
        var startDt = start.ToDateTime(TimeOnly.MinValue);
        var endDt   = end.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var approvedByUser = (await db.ApprovalRequests
                .Where(a => a.ApprovalStatus == ApprovalStatus.Approved
                         && a.SubmittedAt >= startDt && a.SubmittedAt < endDt)
                .GroupBy(a => a.ApplicantUserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.UserId, x => x.Count);

        // 下面几张表按"这批人 + 这个月"一次性整批查出来，循环里直接从内存字典取——原来是每个人
        // 各自查一遍数据库（考勤记录、排班、所在考勤组），几百号人一次全量重算就是几百组重复查询，
        // 数据量小的假期表更是每人都重复查一遍同一个月的数据，现在全部改成先批量查、后内存过滤。
        var recordsByUser = (await db.AttendanceRecords
                .Where(r => candidateIds.Contains(r.UserId) && r.WorkDate >= start && r.WorkDate <= end)
                .ToListAsync())
            .GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.ToList());

        var shiftsByUser = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => candidateIds.Contains(a.UserId) && a.WorkDate >= start && a.WorkDate <= end)
                .ToListAsync())
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.WorkDate, a => a.ShiftSchedule));

        // 假期表数据量本来就很小（一个月内的节假日/调休条目），不用按人过滤，下面按各自的考勤组现场筛选，
        // 和原来 CountExpectedWorkdaysAsync 里"按 groupId 过滤"的效果完全一致
        var holidaysInRange = await db.Holidays
            .Where(h => h.HolidayDate >= start && h.HolidayDate <= end)
            .ToListAsync();

        var groupIds   = users.Where(u => u.AttendanceGroupId.HasValue).Select(u => u.AttendanceGroupId!.Value).Distinct().ToList();
        var groupsById = await db.AttendanceGroups.Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id);

        var existingSummaries = await db.MonthlyAttendanceSummaries
            .Where(s => candidateIds.Contains(s.UserId) && s.Year == year && s.Month == month)
            .ToDictionaryAsync(s => s.UserId);

        var defaultDailyHours = appOptions.Value.DefaultDailyWorkHours;

        foreach (var user in users)
        {
            var records     = recordsByUser.GetValueOrDefault(user.Id, []);
            var shiftByDate = shiftsByUser.GetValueOrDefault(user.Id) ?? new Dictionary<DateOnly, ShiftSchedule>();

            // 应出勤天数：从“月初”和“该员工入职日”里取较晚的一天开始算，
            // 避免月中入职的人被算成全月应出勤、导致出勤率虚低。
            var effStart = user.HireDate is { } hd && hd > start ? hd : start;
            var expected = effStart > end ? 0 : CountExpectedWorkdays(effStart, end, user.AttendanceGroupId, holidaysInRange, shiftByDate);

            // 取出已有的汇总，没有就新建
            if (!existingSummaries.TryGetValue(user.Id, out var summary))
            {
                summary = new MonthlyAttendanceSummary { UserId = user.Id, Year = year, Month = month };
                db.MonthlyAttendanceSummaries.Add(summary);
                existingSummaries[user.Id] = summary;
            }

            // 工时/加班：正常情况下每条记录的 ActualWorkHours 在写入时（本地打卡/钉钉同步/补卡审批）就已经算好了，
            // 这里直接求和即可。仅对「历史遗留、写入时还没补算过」的记录（ActualWorkHours 仍是 0 但有上下班时间）
            // 现场补算，并且顺手写回记录本身——这样老数据只要被打开一次月度报表就能自愈，
            // 不会出现「日明细显示 0、月合计却不是 0」这种对不上的情况。
            var group  = user.AttendanceGroupId.HasValue ? groupsById.GetValueOrDefault(user.AttendanceGroupId.Value) : null;
            var lunch  = group?.LunchBreakMinutes  ?? 60;
            var dinner = group?.DinnerBreakMinutes ?? 30;
            decimal totalWork = 0, totalOt = 0;
            foreach (var r in records)
            {
                // 出差/节假日当天工时按审批口径本来就已经定好（标准工时/0），不能因为当天恰好也有
                // 打卡时间就被这里的"老数据补算"顺手覆盖掉。请假不再排除在外：这里本来就是"老数据
                // 按当前规则重新算一遍"的自愈机制，半天假当天如果有真实打卡，按标准工时封顶补算，
                // 全天假的记录重算结果仍然是 0（封顶为 0），不会产生变化——顺带把 09-17 之前支持
                // 半天假之前被整天清零的历史半天假记录，在下次打开月度报表时自动纠正回来。
                if (r.ActualWorkHours <= 0 && r.ClockInTime is { } ci && r.ClockOutTime is { } co && co > ci
                    && r.AttendanceStatus is not (AttendanceStatus.BusinessTrip or AttendanceStatus.Holiday))
                {
                    // 老数据补算工时口径要和写入时一致：早到晚走都不多算钱，加班只认审批（这里不猜、不动 OvertimeHours）。
                    // 缺打卡的窗口直接从记录本身已经存好的 MidCheckResults 里读，不用班次现在的配置反查
                    // （班次配置可能后来改过，用记录当时冻结下来的这份才准确）。
                    shiftByDate.TryGetValue(r.WorkDate, out var shift);
                    // 休息日自己打卡、又没有批准的加班申请，不补算工时——跟本地打卡同一套规则
                    if (r.OvertimeHours <= 0 && await IsNonCompRestDayAsync(db, r.WorkDate, shift, user.AttendanceGroupId))
                    {
                        r.ActualWorkHours = 0;
                    }
                    else
                    {
                        var midCheckResults = shift is not null ? r.MidCheckResults.ParseMidCheckResults() : [];
                        var missedEnds = shift is not null
                            ? ResolveMissedNonLastWindowEnds(r.WorkDate, shift, midCheckResults)
                            : [];
                        var effCi = ClampEffectiveClockIn(r.WorkDate, ci, shift, missedEnds);
                        var effCo = ClampEffectiveClockOut(r.WorkDate, co, shift, ResolveSecondHalfAbsentBoundary(r.WorkDate, shift, midCheckResults));
                        var computedHours = ComputeWorkHours(effCi, effCo, lunch, dinner);
                        r.ActualWorkHours = r.AttendanceStatus == AttendanceStatus.OnLeave
                            ? ApplyLeaveHoursCap(computedHours, r.LeaveHours, ResolveDailyStandardHours(shift, appOptions.Value.DefaultDailyWorkHours))
                            : computedHours;
                    }
                }
                // 每天的工时/加班按"半小时"为最小单位取整后再累加（不足半小时舍去），
                // 保证月度报表上的合计只会是整数或 x.5，不会出现 113.38 这种零碎小数
                totalWork += FloorToHalf(r.ActualWorkHours);
                totalOt   += FloorToHalf(r.OvertimeHours);
            }

            // 根据每日记录算出各项统计
            summary.ExpectedWorkdays  = expected;
            // 实际出勤 = 打了上班卡的天数 + 已批准出差的天数（旷工/未打卡都不算出勤）；
            // 请假当天如果也有真实打卡（半天假），只算"1 − 请假占比"那一部分出勤，不再跟 LeaveDays
            // 重复记满整天——不然半天假的人会变成"出勤 1 天 + 请假 0.5 天"，一天算出 1.5 天（发现于
            // 2026-09-18 发工资前的数据核查）。跟 GenerateTemplateReportAsync 共用 ResolveAttendanceDayCredit。
            summary.ActualWorkdays = records.Sum(r =>
                ResolveAttendanceDayCredit(r, ResolveDailyStandardHours(shiftByDate.GetValueOrDefault(r.WorkDate), defaultDailyHours)));
            // 迟到/早退按「状态」统计（钉钉同步只写状态、不写分钟数，按分钟数会漏算）
            summary.LateCount         = records.Count(r => r.AttendanceStatus == AttendanceStatus.Late);
            summary.EarlyLeaveCount   = records.Count(r => r.AttendanceStatus == AttendanceStatus.EarlyLeave);
            summary.AbsentDays        = records.Count(r => r.AttendanceStatus == AttendanceStatus.Absent);
            // 缺卡：状态=未打卡，或“只打了上/下班其中一次”（这样钉钉数据的缺卡也能识别；出差本就不用打卡，排除）
            summary.NotPunchedCount   = records.Count(r => r.AttendanceStatus == AttendanceStatus.NotPunched
                || ((r.ClockInTime.HasValue ^ r.ClockOutTime.HasValue)
                    && r.AttendanceStatus is not AttendanceStatus.Absent and not AttendanceStatus.OnLeave
                                          and not AttendanceStatus.Holiday and not AttendanceStatus.BusinessTrip));
            // 请假天数：按小时折算，不再是"这天状态是请假就算一整天"——半天假只占 0.5 天
            // （2026-09-17 支持半天请假）。
            summary.LeaveDays = records.Where(r => r.AttendanceStatus == AttendanceStatus.OnLeave)
                .Sum(r => ResolveLeaveDaysFraction(r.LeaveHours, ResolveDailyStandardHours(shiftByDate.GetValueOrDefault(r.WorkDate), defaultDailyHours)));
            summary.TotalOvertimeHours = totalOt;
            summary.TotalWorkHours    = totalWork;
            summary.ApprovedCount     = approvedByUser.GetValueOrDefault(user.Id);   // 本月审批通过次数（原来漏算，恒为 0）
            summary.UpdatedAt         = DateTime.Now;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>"我的记录"/"我的日历"共用：确保这个人这个月的汇总是新鲜的，不是每次打开页面都无条件
    /// 重算一遍——"我的日历"以前是每次 GET 都调 GenerateMonthlySummaryAsync，哪怕数据毫无变化也会
    /// 产生一次 UPDATE；"我的记录"则完全不重算，只读现有汇总行，当月还没生成过汇总时会显示空白，
    /// 跟"我的日历"（强制重算，总有数据）表现不一致，容易让人以为哪个页面出了 bug（2026-09-21
    /// 代码审查发现，见 docs/项目审查与问题总表.md B3/C3）。</summary>
    public async Task EnsureMonthlySummaryFreshAsync(int userId, int year, int month)
    {
        var summary = await db.MonthlyAttendanceSummaries
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Year == year && s.Month == month);
        if (summary is null)
        {
            await GenerateMonthlySummaryAsync(year, month, [userId]);
            return;
        }

        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);
        var latestRecordUpdate = await db.AttendanceRecords
            .Where(r => r.UserId == userId && r.WorkDate >= start && r.WorkDate <= end)
            .Select(r => (DateTime?)r.UpdatedAt)
            .MaxAsync() ?? DateTime.MinValue;
        if (latestRecordUpdate > summary.UpdatedAt)
            await GenerateMonthlySummaryAsync(year, month, [userId]);
    }

    /// <summary>今日考勤看板统计（出勤/旷工/迟到/请假/未打卡人数）。</summary>
    public async Task<AttendanceStatsDto> GetTodayStatsAsync(int? groupId = null, HashSet<int>? deptIds = null)
    {
        var today   = DateOnly.FromDateTime(DateTime.Today);
        var userIds = await BuildUserIdQueryAsync(null, groupId, deptIds);
        var records = await db.AttendanceRecords
            .Where(r => r.WorkDate == today && userIds.Contains(r.UserId))
            .ToListAsync();

        // "没出勤"的人里，旷工/请假/节假日已经各有自己的口径和卡片了，"未打卡"这张卡只应该统计
        // 剩下那批"今天还没来打卡、但又不属于旷工/请假/节假日"的人（比如上午还没到岗），
        // 不然旷工当天晚上 23:55 被后台任务标记成 Absent 之后，同一个人会同时被"旷工"和"未打卡"
        // 两张卡各数一遍，两个数字加起来会比总人数还多，看板数据对不上。
        var accountedForCount = records.Count(r =>
            r.AttendanceStatus is AttendanceStatus.Absent or AttendanceStatus.OnLeave or AttendanceStatus.Holiday);

        return new AttendanceStatsDto
        {
            StatsDate       = today,
            TotalEmployees  = userIds.Count,
            PresentCount    = records.Count(IsPresent),   // 打了上班卡，或已批准出差（无需打卡也算全勤）
            AbsentCount     = records.Count(r => r.AttendanceStatus == AttendanceStatus.Absent),
            // 迟到按「状态」统计（与缺勤/请假口径一致）：钉钉同步只写状态不写迟到分钟数，
            // 若按 LateMinutes>0 算会漏掉钉钉来的迟到。
            LateCount       = records.Count(r => r.AttendanceStatus == AttendanceStatus.Late),
            OnLeaveCount    = records.Count(r => r.AttendanceStatus == AttendanceStatus.OnLeave),
            // 总人数 - 出勤 - 旷工/请假/节假日 = 剩下"还没打卡、原因待定"的人，不和旷工/请假重复计数
            NotPunchedCount = userIds.Count - records.Count(IsPresent) - accountedForCount,
            LocationAbnormalCount = records.Count(r => r.LocationAbnormal)
        };
    }

    /// <summary>判断某天算不算“出勤”：打了上班卡，或者当天已批准出差（出差无需打卡也算全勤）。</summary>
    private static bool IsPresent(AttendanceRecord r) => r.ClockInTime.HasValue || r.AttendanceStatus == AttendanceStatus.BusinessTrip;

    /// <summary>
    /// 这一天该算多少"出勤天数"：完全没到岗（含整天请假）算 0；请假当天如果也有真实打卡（半天假），
    /// 按"1 − 请假占比"算部分出勤，不再跟请假天数重复记满整天；其余（含出差全勤）算 1 整天。
    /// ★ 全系统唯一口径：<see cref="GenerateMonthlySummaryAsync"/>（月度汇总/我的记录页）和
    /// <see cref="GenerateTemplateReportAsync"/>（模板汇总表，发工资用的那份导出）必须共用这一个方法，
    /// 不能各写一份——之前就是因为两处各算各的，同一个人同一个月两份报表的出勤天数对不上
    /// （发现于 2026-09-18 数据核查）。
    /// </summary>
    public static decimal ResolveAttendanceDayCredit(AttendanceRecord r, decimal standardHours)
    {
        if (!IsPresent(r)) return 0m;
        if (r.AttendanceStatus != AttendanceStatus.OnLeave) return 1m;
        return Math.Max(0m, 1m - ResolveLeaveDaysFraction(r.LeaveHours, standardHours));
    }

    /// <summary>
    /// 看板下钻：某统计类别对应的具体人员名单。分类口径和 <see cref="GetTodayStatsAsync"/> 完全一致
    /// （同一批 userIds、同一批 records、同样的判断条件），保证卡片上的数字和点开后名单的人数永远对得上。
    /// </summary>
    public async Task<List<AttendanceRecordDto>> GetTodayStatsDetailAsync(string category, int? groupId = null, HashSet<int>? deptIds = null)
    {
        var today   = DateOnly.FromDateTime(DateTime.Today);
        var userIds = await BuildUserIdQueryAsync(null, groupId, deptIds);

        var users = await db.Users.Include(u => u.Department)
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);

        var records = await db.AttendanceRecords
            .Where(r => r.WorkDate == today && userIds.Contains(r.UserId))
            .ToListAsync();
        var recordByUser = records.ToDictionary(r => r.UserId);

        // 挑出属于该类别的用户 id：判断条件必须和 GetTodayStatsAsync 里一一对应，否则数字会对不上
        var presentIds = records.Where(IsPresent).Select(r => r.UserId).ToHashSet();
        List<int> targetIds = category switch
        {
            "total"      => userIds,
            "present"    => presentIds.ToList(),
            "absent"     => records.Where(r => r.AttendanceStatus == AttendanceStatus.Absent).Select(r => r.UserId).ToList(),
            "late"       => records.Where(r => r.AttendanceStatus == AttendanceStatus.Late).Select(r => r.UserId).ToList(),
            "onleave"    => records.Where(r => r.AttendanceStatus == AttendanceStatus.OnLeave).Select(r => r.UserId).ToList(),
            // 含"完全没记录"和"有记录但没打上班卡"两种人，但排除旷工/请假/节假日——这三类已经各有
            // 自己的卡片，混进"未打卡"会跟"absent"下钻名单里的人重复，和 GetTodayStatsAsync 的口径保持一致
            "notpunched" => userIds.Where(id => !presentIds.Contains(id)
                && (!recordByUser.TryGetValue(id, out var npRec)
                    || npRec.AttendanceStatus is not (AttendanceStatus.Absent or AttendanceStatus.OnLeave or AttendanceStatus.Holiday)))
                .ToList(),
            "locationabnormal" => records.Where(r => r.LocationAbnormal).Select(r => r.UserId).ToList(),
            _            => []
        };

        var result = new List<AttendanceRecordDto>();
        foreach (var uid in targetIds)
        {
            if (!users.TryGetValue(uid, out var user)) continue;   // 理论上不会发生，防御一下
            recordByUser.TryGetValue(uid, out var rec);

            result.Add(new AttendanceRecordDto
            {
                Id               = rec?.Id ?? 0,
                UserId           = uid,
                EmployeeNo       = user.EmployeeNo,
                RealName         = user.RealName,
                DeptName         = user.Department?.DeptName,
                WorkDate         = today,
                ClockInTime      = rec?.ClockInTime,
                ClockOutTime     = rec?.ClockOutTime,
                AttendanceStatus = rec?.AttendanceStatus ?? AttendanceStatus.NotPunched,
                StatusText       = rec is null ? "未打卡（无记录）" : StatusText(rec.AttendanceStatus),
                StatusCssClass   = rec is null ? "f-color-red" : StatusCss(rec.AttendanceStatus),
                LateMinutes      = rec?.LateMinutes ?? 0,
                EarlyLeaveMinutes = rec?.EarlyLeaveMinutes ?? 0,
                ApprovalNote     = rec?.ApprovalNote,
                LocationAbnormal     = rec?.LocationAbnormal ?? false,
                LocationAbnormalNote = rec?.LocationAbnormalNote
            });
        }

        return result
            .OrderBy(r => r.DeptName ?? "")
            .ThenBy(r => r.EmployeeNo)
            .ToList();
    }

    /// <summary>判断某天是否节假日（调班补班日不算节假日）。</summary>
    public Task<bool> IsHolidayAsync(DateOnly date, int? groupId = null)
        => db.Holidays.AnyAsync(h =>
            h.HolidayDate == date &&
            h.HolidayType != HolidayType.CompensatoryWorkDay &&
            (h.AttendanceGroupId == null || h.AttendanceGroupId == groupId));

    /// <summary>
    /// 这天是不是排的班次自己配置的每周休息日；没排班时按全局周六周日兜底。不含"调班补班日"和
    /// "法定节假日/公司休息"的判断——那两类调用方各自按自己的场景处理（调班日通常要覆盖这个结果，
    /// 法定节假日/公司休息则是完全独立的另一个概念，见 IsHolidayAsync）。
    /// ★ 全系统唯一口径：<see cref="CountExpectedWorkdaysAsync"/>、<see cref="GenerateTemplateReportAsync"/>、
    /// <see cref="IsNonCompRestDayAsync"/>、AttendanceBackgroundService.MarkAbsentAsync 都调这一个，
    /// 避免同一条"是不是休息日"的规则散落成好几份、以后改规则漏改一处（MarkAbsentAsync 以前用的是
    /// "周六周日一律跳过"的粗口径，没有并进来，导致六天倒班、周六照常排班的员工周六没来也没请假会
    /// 被漏判旷工，"应出勤"却仍然算这天——2026-09-21 已经统一）。
    /// </summary>
    public static bool IsShiftWeeklyRestDay(DateOnly date, ShiftSchedule? shift) =>
        shift is not null ? shift.IsRestDay(date.DayOfWeek) : date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>
    /// 判断某天对这个员工算不算"休息日"——只用来决定"没有批准的加班申请就不计工时"，不影响能不能打卡。
    /// 法定节假日已经在打卡入口直接拒绝打卡了（见 PunchCoreAsync 的 IsHolidayAsync 判断），走到这里
    /// 只需要看两种情况：排的班次自己配置的每周休息日，或者没排班时按全局周六周日兜底；调班补班日
    /// 不算休息日（公司要求上班，工时照常算）。
    /// </summary>
    public static async Task<bool> IsNonCompRestDayAsync(AttendanceDbContext db, DateOnly date, ShiftSchedule? shift, int? groupId)
    {
        var isCompDay = await db.Holidays.AnyAsync(h =>
            h.HolidayDate == date &&
            h.HolidayType == HolidayType.CompensatoryWorkDay &&
            (h.AttendanceGroupId == null || h.AttendanceGroupId == groupId));
        return !isCompDay && IsShiftWeeklyRestDay(date, shift);
    }

    /// <summary>取某月的假期信息（全公司 + 该考勤组专属的），供日历页标注法定节假日/公司休息日/调班补班日。</summary>
    public async Task<List<HolidayInfoDto>> GetMonthHolidaysAsync(int year, int month, int? groupId)
    {
        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);
        return await db.Holidays
            .Where(h => h.HolidayDate >= start && h.HolidayDate <= end
                     && (h.AttendanceGroupId == null || h.AttendanceGroupId == groupId))
            .Select(h => new HolidayInfoDto { Date = h.HolidayDate, Name = h.HolidayName, Type = h.HolidayType })
            .ToListAsync();
    }

    /// <summary>取某员工某天的排班（含班次信息）。</summary>
    public Task<ShiftAssignment?> GetShiftAssignmentAsync(int userId, DateOnly date)
        => db.ShiftAssignments
             .Include(a => a.ShiftSchedule)
             .FirstOrDefaultAsync(a => a.UserId == userId && a.WorkDate == date);

    /// <summary>
    /// 给 ApprovalNote 追加一条新的审批说明，而不是直接覆盖——同一天可能先后批了不同类型的审批
    /// （比如先批了请假、后来又批了加班），如果直接覆盖，先写的那条说明会凭空消失，明明这天两件事
    /// 都批了，备注却只看得出后写的那一件。已有内容就用"；"隔开拼在后面，没有就直接写。
    /// ApprovalNote 数据库列是 [MaxLength(200)]（AttendanceRecord.cs），改成"追加"之后，同一天
    /// 反复手动补卡（每次备注控制在 100 字以内，见 AdminAdjustPunchAsync 的校验）叠加几次就有可能
    /// 超过 200 字上限，SaveChangesAsync 会直接报数据库层面的"数据太长"错误——这里在拼接后统一截断到
    /// 200 字以内（截断时带上省略号，不会悄无声息地丢内容却看不出来），当最后一道安全网。
    /// </summary>
    internal static void AppendApprovalNote(AttendanceRecord record, string note)
    {
        var combined = string.IsNullOrEmpty(record.ApprovalNote) ? note : $"{record.ApprovalNote}；{note}";
        record.ApprovalNote = combined.Length > 200 ? combined[..197] + "..." : combined;
    }

    /// <summary>
    /// 审批通过后回写考勤：补卡 → 补填上/下班时间；加班 → 按审批单时长累加加班工时（加班不再从
    /// 打卡时间估算，必须走这里）；请假/出差 → 把区间内每天置为对应状态。只处理状态为「已通过」的申请。
    /// </summary>
    public async Task UpdateAttendanceAfterApprovalAsync(int approvalRequestId)
    {
        var approval = await db.ApprovalRequests.FindAsync(approvalRequestId);
        if (approval is null || approval.ApprovalStatus != ApprovalStatus.Approved) return;

        // 记下这次改动实际动到了哪些"年-月"，回写完之后要把这些月份的月度汇总同步刷新一下
        // （不然月初已经生成过的汇总，不会因为后补的记录自动更新，得靠人工点"重新生成"才会准）。
        var touchedMonths = new HashSet<(int Year, int Month)>();

        // ── 补卡 ──
        if (approval.ApprovalType == ApprovalType.PunchReplenishment && approval.PunchDate.HasValue
            && approval.PunchTime.HasValue)
        {
            var record = await db.AttendanceRecords
                .FirstOrDefaultAsync(r => r.UserId == approval.ApplicantUserId
                                       && r.WorkDate == approval.PunchDate.Value);
            if (record is null)
            {
                // 当天完全没有记录也要新建一条：审批都通过了，补卡不能被静默忽略
                record = new AttendanceRecord { UserId = approval.ApplicantUserId, WorkDate = approval.PunchDate.Value };
                db.AttendanceRecords.Add(record);
            }

            var punchDt = approval.PunchDate.Value.ToDateTime(approval.PunchTime.Value);
            if (approval.PunchType == PunchType.ClockIn) record.ClockInTime  = punchDt;   // 补上班卡
            else                                          record.ClockOutTime = punchDt;   // 补下班卡
            AppendApprovalNote(record, $"补卡已审批通过（{approval.RequestNo}）");
            record.UpdatedAt    = DateTime.Now;

            // 补齐上下班两次卡后：重算当天实际工时（工资按工时结算，补完卡必须把工时补准），
            // 并解除“旷工/未打卡”状态（否则人有全天工时却仍被记旷工，工资和出勤对不上）。
            await RecalcWorkHoursAfterManualPunchAsync(record, approval.ApplicantUserId);
            touchedMonths.Add((approval.PunchDate.Value.Year, approval.PunchDate.Value.Month));
        }
        // ── 加班 ──：加班时长完全以审批单为准，不从打卡时间估算；累加到当天的加班时长上
        // （同一天可能有多张已批准的加班单，所以是加，不是覆盖）
        else if (approval.ApprovalType == ApprovalType.Overtime && approval.OvertimeStartTime.HasValue
                 && approval.OvertimeDurationHours is > 0)
        {
            var workDate = DateOnly.FromDateTime(approval.OvertimeStartTime.Value);
            var record = await db.AttendanceRecords
                .FirstOrDefaultAsync(r => r.UserId == approval.ApplicantUserId && r.WorkDate == workDate);
            if (record is null)
            {
                record = new AttendanceRecord { UserId = approval.ApplicantUserId, WorkDate = workDate };
                db.AttendanceRecords.Add(record);
            }

            record.OvertimeHours += approval.OvertimeDurationHours.Value;
            AppendApprovalNote(record, $"加班已审批通过（{approval.RequestNo}），{approval.OvertimeDurationHours:0.##} 小时");
            record.UpdatedAt      = DateTime.Now;

            // 员工可能是先自己打卡上班、事后才补的加班申请——这天如果是休息日，打卡时因为
            // 当时还没有批准的加班申请，工时会被算成 0（见 ComputeDailyWorkHoursAsync）；
            // 现在加班批下来了，要把已有的打卡重新算一遍，把工时补回来。
            await RecalcWorkHoursAfterManualPunchAsync(record, approval.ApplicantUserId);
            touchedMonths.Add((workDate.Year, workDate.Month));
        }
        // ── 请假 ──
        else if (approval.ApprovalType == ApprovalType.Leave && approval.LeaveStartTime.HasValue)
        {
            var sd = DateOnly.FromDateTime(approval.LeaveStartTime.Value);
            var leaveEnd = approval.LeaveEndTime ?? approval.LeaveStartTime.Value;
            var ed = DateOnly.FromDateTime(leaveEnd);

            // 先把请假区间内已经存在的考勤记录一次性整批查出来，按"日期"放进一个字典。
            // 原来的写法是下面 for 循环里每天单独查一次数据库——请十天假就要查十次数据库，
            // 现在改成先查一次、缓存到内存里，下面循环直接从内存里找，结果完全一样，只是数据库跑得更少更快。
            var recordsInRange = await db.AttendanceRecords
                .Where(r => r.UserId == approval.ApplicantUserId && r.WorkDate >= sd && r.WorkDate <= ed)
                .ToDictionaryAsync(r => r.WorkDate);

            // 算每天请假时长要用到考勤组的午休/晚餐扣时（跟 LeaveDurationHours 提交时用的同一套算法），
            // 以及这天排的班次的标准工时（没排班就用公司默认标准工时）给 ComputeLeaveHoursForDay 封顶用，
            // 都只查一次，下面循环里每天复用。
            var applicant = await db.Users.FindAsync(approval.ApplicantUserId);
            var leaveGroup = applicant?.AttendanceGroupId.HasValue == true
                ? await db.AttendanceGroups.FindAsync(applicant.AttendanceGroupId.Value) : null;
            var leaveShiftsInRange = (await db.ShiftAssignments
                    .Include(a => a.ShiftSchedule)
                    .Where(a => a.UserId == approval.ApplicantUserId && a.WorkDate >= sd && a.WorkDate <= ed)
                    .ToListAsync())
                .ToDictionary(a => a.WorkDate, a => a.ShiftSchedule);
            var defaultDailyHours = appOptions.Value.DefaultDailyWorkHours;

            for (var d = sd; d <= ed; d = d.AddDays(1))   // 请假区间内每一天
            {
                // 请假时长只算落在"这一天"里的那一段——跨天请假的第一天/最后一天可能不是整天
                // （比如 09-09 14:00 请假到 09-11 12:00，09-09 当天只算 14:00~24:00 这一段），
                // 用 ComputeLeaveHoursForDay 取交集再扣午休/晚餐，并封顶在这天的标准工时。
                // 要不要处理这一天，用"是否有真实交集"判断（HasLeaveOverlapForDay），不能用
                // "算出来的小时数是不是 0"判断——如果结束时间恰好卡在午夜 0 点（比如请假到
                // 9-11 00:00），区间最后一天（9-11）跟请假时段确实没有任何交集，这种天才该跳过、
                // 不新建记录、不标"请假"状态；但如果只是交集很短（比如请假 20 分钟，取整成 0 小时），
                // 这一天仍然有真实交集，应该照常标记"请假"，只是 LeaveHours 恰好是 0——不然会把
                // 短时长请假误判成没有交集直接跳过，这天没打卡的话会被后台旷工任务误标成旷工
                // （发现于 2026-09-18：这是 09-18 那次"无交集跳过"修复自身遗留的边界缺陷）。
                if (!HasLeaveOverlapForDay(d, approval.LeaveStartTime.Value, leaveEnd)) continue;

                var dailyCap = leaveShiftsInRange.TryGetValue(d, out var leaveShift)
                    ? leaveShift.StandardWorkHours : defaultDailyHours;
                var leaveHoursToday = ComputeLeaveHoursForDay(d, approval.LeaveStartTime.Value, leaveEnd,
                    leaveGroup?.LunchBreakMinutes ?? 60, leaveGroup?.DinnerBreakMinutes ?? 30, dailyCap);

                // 当天完全没有记录也要新建一条（比如请的是未来的假、这天还没产生任何打卡数据）——
                // 不然等到这天真过完，后台"旷工检查"任务会因为查不到记录，把已经批准的请假误标记成旷工。
                if (!recordsInRange.TryGetValue(d, out var record))
                {
                    record = new AttendanceRecord { UserId = approval.ApplicantUserId, WorkDate = d };
                    db.AttendanceRecords.Add(record);
                    recordsInRange[d] = record;
                }

                record.AttendanceStatus = AttendanceStatus.OnLeave;
                // 先清零打底，再看这天有没有真实打卡——没有打卡（纯请假一整天）就保持 0；
                // 有打卡（半天假场景：上午上班、下午请假之类）交给下面的 RecalcWorkHoursAfterManualPunchAsync
                // 按"标准工时 − 已批准请假小时数"重新封顶结算，不再是无条件清零（2026-09-17 支持半天请假）。
                record.ActualWorkHours   = 0;
                record.LateMinutes       = 0;
                record.EarlyLeaveMinutes = 0;
                // 累加而不是覆盖：同一天可能先后批了两张假单（比如上午一张、下午一张），
                // 覆盖会让后批的那张顶掉先批的，天数折算（GenerateMonthlySummaryAsync 里的
                // LeaveDays）就会少算（2026-09-17 支持半天请假时一并修复）
                record.LeaveHours += leaveHoursToday;

                // 这天如果已经有真实打卡（半天假），按最新的 LeaveHours 重新结算工时/迟到/早退——
                // 跟"管理员手动补卡"复用同一个函数，两条路径的"请假封顶"口径自动保持一致；
                // 状态本身不会被这个函数改回正常/迟到/早退，上面设的 OnLeave 会保留。
                await RecalcWorkHoursAfterManualPunchAsync(record, approval.ApplicantUserId);

                // 半天假最常见的场景是"上午上班、下午请假"——员工打了上班卡，但因为下午走了，
                // 通常不会再打下班卡。上面那个 RecalcWorkHoursAfterManualPunchAsync 要求同时有
                // 上下班卡才会重算，这种情况下会直接跳过不处理，ActualWorkHours 停在上面设的
                // 占位值 0，上午真实工作的工时就永久丢失了、也没有任何自愈机制能补回来。
                // 这里用"上班卡 → 这天请假开始的时间点"估算这一段真实工作时长，按标准工时封顶
                // （发现于 2026-09-18 数据核查）。
                if (record.ClockInTime is { } workedCi && record.ClockOutTime is null)
                {
                    var dayStart      = d.ToDateTime(TimeOnly.MinValue);
                    var leaveSegStart = approval.LeaveStartTime.Value > dayStart ? approval.LeaveStartTime.Value : dayStart;
                    if (workedCi < leaveSegStart)
                    {
                        var estimatedWork = ComputeWorkHours(workedCi, leaveSegStart,
                            leaveGroup?.LunchBreakMinutes ?? 60, leaveGroup?.DinnerBreakMinutes ?? 30);
                        record.ActualWorkHours = ApplyLeaveHoursCap(estimatedWork, record.LeaveHours, dailyCap);
                    }
                }

                // 备注带上这一天的请假时长，跟加班审批的备注格式一致（"加班已审批通过（单号），9 小时"）
                AppendApprovalNote(record, $"请假审批通过（{approval.RequestNo}），{leaveHoursToday:0.##} 小时");
                record.UpdatedAt        = DateTime.Now;
                touchedMonths.Add((d.Year, d.Month));
            }
        }
        // ── 出差 ──：出差期间不用打卡，逐天置为「出差」并按全勤记工时（工资按工时结算，不能漏记）
        else if (approval.ApprovalType == ApprovalType.BusinessTrip && approval.BusinessTripStartTime.HasValue)
        {
            var sd = DateOnly.FromDateTime(approval.BusinessTripStartTime.Value);
            var ed = DateOnly.FromDateTime(approval.BusinessTripEndTime ?? approval.BusinessTripStartTime.Value);
            var defaultHours = appOptions.Value.DefaultDailyWorkHours;

            // 和上面请假的道理一样：把这段时间已有的考勤记录、以及已有的排班，
            // 都先各查一次整批拿出来，下面循环里直接从内存查，不用每天都各查一次数据库
            // （原来一趟出差要查 2×天数 次数据库，现在固定只查 2 次）。
            var recordsInRange = await db.AttendanceRecords
                .Where(r => r.UserId == approval.ApplicantUserId && r.WorkDate >= sd && r.WorkDate <= ed)
                .ToDictionaryAsync(r => r.WorkDate);
            var shiftsInRange = await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => a.UserId == approval.ApplicantUserId && a.WorkDate >= sd && a.WorkDate <= ed)
                .ToDictionaryAsync(a => a.WorkDate);

            for (var d = sd; d <= ed; d = d.AddDays(1))   // 出差区间内每一天
            {
                if (!recordsInRange.TryGetValue(d, out var record))
                {
                    record = new AttendanceRecord { UserId = approval.ApplicantUserId, WorkDate = d };
                    db.AttendanceRecords.Add(record);
                    recordsInRange[d] = record;   // 也放进字典，避免万一日期算重了会重复新建
                }

                // 有排班就用班次自己的标准工时，没排班就用默认标准工时（不用打卡也要按全勤给工时）
                shiftsInRange.TryGetValue(d, out var shiftAssign);
                record.AttendanceStatus  = AttendanceStatus.BusinessTrip;
                record.ActualWorkHours   = shiftAssign?.ShiftSchedule.StandardWorkHours ?? defaultHours;
                // 迟到/早退分钟数清零，理由同请假分支：不然出差前如果已经有过打卡产生的分钟数，会残留在报表里
                record.LateMinutes       = 0;
                record.EarlyLeaveMinutes = 0;
                // OvertimeHours 不动：出差是否加班无法从审批单推断，不猜——但也不能不管三七二十一直接清零，
                // 万一这天之前已经有另一张加班申请审批通过、累加过加班时长，这里清零会把已批准的加班顶掉。
                // 新建的记录本来就是 0（实体默认值），不用特意再赋一次。
                AppendApprovalNote(record, $"出差审批通过（{approval.RequestNo}）"
                    + (string.IsNullOrWhiteSpace(approval.BusinessTripDestination) ? "" : $"，目的地：{approval.BusinessTripDestination}"));
                record.UpdatedAt        = DateTime.Now;
                touchedMonths.Add((d.Year, d.Month));
            }
        }

        await db.SaveChangesAsync();

        // 同步刷新受影响月份的月度汇总，不用再等人工点"重新生成"（GenerateMonthlySummaryAsync 本身是幂等的）
        foreach (var (y, m) in touchedMonths)
            await GenerateMonthlySummaryAsync(y, m, [approval.ApplicantUserId]);
    }

    /// <summary>
    /// 管理员手动补卡：最高权限，不受员工"补卡申请"审批流程的任何限制——可以给任意员工、任意日期、
    /// 任意情况下直接补录/修改打卡时间，立即生效，不用走审批。工时重算口径和审批通过后的补卡完全一致
    /// （见 <see cref="RecalcWorkHoursAfterManualPunchAsync"/>），保证走这条路径和走审批路径算出来的工时对得上。
    /// </summary>
    public async Task AdminAdjustPunchAsync(int userId, DateOnly workDate, DateTime? clockIn, DateTime? clockOut, string? remark, string? operatorName)
    {
        // 备注长度校验：ApprovalNote 数据库列上限 200 字，现在改成"追加"而不是覆盖后，同一天反复
        // 手动补卡会不断累加，单次备注控制在 100 字以内才留得出余量给后面可能追加的其它说明
        // （AppendApprovalNote 里还有一道 200 字截断兜底，这里是提前给管理员一个看得懂的提示，
        // 而不是让内容被静默截断）
        if (!string.IsNullOrWhiteSpace(remark) && remark.Trim().Length > 100)
            throw new InvalidOperationException("补卡备注不能超过 100 个字");

        var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == workDate);
        if (record is null)
        {
            record = new AttendanceRecord { UserId = userId, WorkDate = workDate };
            db.AttendanceRecords.Add(record);
        }

        if (clockIn.HasValue)  record.ClockInTime  = clockIn.Value;
        if (clockOut.HasValue) record.ClockOutTime = clockOut.Value;
        // 备注里带上操作人姓名，留下痕迹，方便"手动补卡"页面下方的操作记录列表追溯是谁改的。
        // 用 AppendApprovalNote 追加而不是直接覆盖——这天如果之前已经有请假/加班/出差审批通过的备注，
        // 手动补卡不该把那条记录抹掉，不然事后没法还原这天到底发生过什么（2026-09-17 代码审查发现）
        var opText = string.IsNullOrWhiteSpace(operatorName) ? "管理员手动补卡" : $"管理员手动补卡（操作人：{operatorName}）";
        AppendApprovalNote(record, string.IsNullOrWhiteSpace(remark) ? opText : $"{opText}：{remark.Trim()}");
        record.UpdatedAt    = DateTime.Now;

        await RecalcWorkHoursAfterManualPunchAsync(record, userId);
        await db.SaveChangesAsync();

        // 同步刷新这个月的月度汇总，道理和审批回写那边一样，不用等人工点"重新生成"
        await GenerateMonthlySummaryAsync(workDate.Year, workDate.Month, [userId]);
    }

    // ── 私有计算方法（下面这些只在本服务内部使用）─────────────────────────────────

    /// <summary>
    /// 补卡后（不管是审批通过回写，还是管理员手动补卡）重算当天实际工时，并解除"旷工/未打卡"状态
    /// （否则人有全天工时却仍被记旷工，工资和出勤对不上）。上下班两次卡都有且下班晚于上班才会重算；
    /// 加班不再从打卡时间估算，只认「加班申请」审批通过后累加的时长，这里不动 OvertimeHours。
    /// </summary>
    /// <summary>上下班卡都有、但下班时间早于或等于上班时间（时间倒挂，通常是手动补卡填反了，或者
    /// 设备/客户端时钟异常）时用来提醒的说明文字——这种记录不满足"两个 null 判断"，之前会被当天
    /// 补卡后的三处工时结算逻辑直接跳过，工时永远停在占位值 0，且不出现在任何异常统计里，管理员
    /// 完全看不出这天有问题（2026-09-21 代码审查发现）。这里不猜"最终有效时间"去强行重算，只是
    /// 把异常显式记进 ApprovalNote，让管理员能看到、去人工核实；用 Contains 判重，避免同一条记录
    /// 每次触发结算都重复追加同一句话。</summary>
    internal const string ClockTimeInvertedNote = "打卡时间异常（下班时间早于或等于上班时间），工时未结算，需人工核实";

    private async Task RecalcWorkHoursAfterManualPunchAsync(AttendanceRecord record, int userId)
    {
        if (record.ClockInTime.HasValue && record.ClockOutTime is { } coRaw && coRaw <= record.ClockInTime.Value
            && (record.ApprovalNote is null || !record.ApprovalNote.Contains(ClockTimeInvertedNote)))
        {
            AppendApprovalNote(record, ClockTimeInvertedNote);
        }
        if (record.ClockInTime is not { } ci || record.ClockOutTime is not { } co || co <= ci) return;

        var applicant   = await db.Users.FindAsync(userId);
        var shiftAssign = await GetShiftAssignmentAsync(userId, record.WorkDate);
        var shift       = shiftAssign?.ShiftSchedule;

        // 出差/节假日这两个状态当天的工时/迟到/早退都已经由审批流程/定时任务定好了，不能被这次补卡
        // 顺手重算覆盖掉。请假不再整天排除在外：半天假当天如果还有真实打卡，按标准工时封顶结算，
        // 迟到/早退分钟数也照算——只有"状态本身"不能被打卡结果改回正常/迟到/早退，这天终归还是
        // "请假"（2026-09-17 支持半天请假，两道保护拆成两个变量分别控制）。
        var blocksWorkHours       = record.AttendanceStatus is AttendanceStatus.Holiday or AttendanceStatus.BusinessTrip;
        var blocksStatusOverwrite = record.AttendanceStatus is
            AttendanceStatus.OnLeave or AttendanceStatus.Holiday or AttendanceStatus.BusinessTrip;

        // 工时口径和本地打卡一致：早到晚走都不多算钱，班次配了午间必打卡窗口、当天又没有打卡落在窗口内，只算下午；
        // 休息日没有批准的加班申请也不算工时（跟本地打卡同一套规则，见 ComputeDailyWorkHoursAsync）
        if (!blocksWorkHours)
        {
            var computedHours = await ComputeDailyWorkHoursAsync(record, record.WorkDate, ci, co, shift, applicant?.AttendanceGroupId);
            record.ActualWorkHours = record.AttendanceStatus == AttendanceStatus.OnLeave
                ? ApplyLeaveHoursCap(computedHours, record.LeaveHours, ResolveDailyStandardHours(shift, appOptions.Value.DefaultDailyWorkHours))
                : computedHours;
        }

        // 迟到/早退分钟数和状态都要按补卡后的新时间重新算一遍，不能沿用改之前的旧值——
        // 不然管理员把一条迟到记录的上班时间改准点了，LateMinutes 还留着旧的迟到分钟数、
        // 状态也可能继续显示"迟到"。出差/节假日这两个审批流程/定时任务设置的状态不受影响。
        var isRestDay      = await IsNonCompRestDayAsync(db, record.WorkDate, shift, applicant?.AttendanceGroupId);
        var clockInStatus  = CalcClockInStatus(ci, shift, isRestDay, out var lateMin);
        var clockOutStatus = CalcClockOutStatus(record.WorkDate, co, shift, isRestDay, out var earlyMin);
        if (!blocksWorkHours)
        {
            record.LateMinutes       = lateMin;
            record.EarlyLeaveMinutes = earlyMin;
        }
        if (!blocksStatusOverwrite)
        {
            record.AttendanceStatus  = clockInStatus == AttendanceStatus.Late   ? AttendanceStatus.Late
                                      : clockOutStatus == AttendanceStatus.EarlyLeave ? AttendanceStatus.EarlyLeave
                                      : AttendanceStatus.Normal;
        }
    }

    /// <summary>
    /// 算上班状态：实际打卡比「应上班时间 + 迟到容忍」还晚就算迟到。没排班、或者今天是这个员工的
    /// 休息日（<paramref name="isRestDay"/>，不管有没有批加班——休息日没有"应上班时间"可比，
    /// 谈不上迟到）一律算正常。out lateMinutes 把迟到分钟数“带出去”给调用者。
    /// </summary>
    internal static AttendanceStatus CalcClockInStatus(
        DateTime clockIn, ShiftSchedule? shift, bool isRestDay, out int lateMinutes)
    {
        lateMinutes = 0;
        if (shift is null || isRestDay) return AttendanceStatus.Normal;
        var scheduled = DateOnly.FromDateTime(clockIn).ToDateTime(shift.WorkStartTime);   // 应上班时刻
        var diff      = (int)(clockIn - scheduled).TotalMinutes;                          // 晚了几分钟
        if (diff > shift.LateToleranceMinutes) { lateMinutes = diff; return AttendanceStatus.Late; }
        return AttendanceStatus.Normal;
    }

    /// <summary>
    /// 算下班状态：实际打卡比「应下班时间 − 早退容忍」还早就算早退。夜班的下班时间顺延一天；
    /// 今天是这个员工的休息日（<paramref name="isRestDay"/>）一律算正常，理由同 <see cref="CalcClockInStatus"/>。
    /// out earlyMinutes 把早退分钟数带出去。
    /// ★ "应下班时刻"以 <paramref name="workDate"/>（这条考勤记录归属的那一天，即班次开始的那天）为基准推算，
    /// 不能用 clockOut 打卡那一刻的日期——跨天班次下班时打卡已经是第二天了，
    /// 拿打卡当天的日期再顺延一天会多算出一整天，导致应下班时刻算错。
    /// </summary>
    internal static AttendanceStatus CalcClockOutStatus(
        DateOnly workDate, DateTime clockOut, ShiftSchedule? shift, bool isRestDay, out int earlyMinutes)
    {
        earlyMinutes = 0;
        if (shift is null || isRestDay) return AttendanceStatus.Normal;
        var scheduled = workDate.ToDateTime(shift.WorkEndTime);   // 应下班时刻
        if (shift.IsCrossDay) scheduled = scheduled.AddDays(1);   // 夜班顺延到 workDate 的第二天
        var diff = (int)(scheduled - clockOut).TotalMinutes;      // 早走了几分钟
        if (diff > shift.EarlyLeaveToleranceMinutes) { earlyMinutes = diff; return AttendanceStatus.EarlyLeave; }
        return AttendanceStatus.Normal;
    }

    /// <summary>
    /// 算实际工时（小时）：上下班时间差，再扣午休/晚餐。
    /// 规则：超过 6 小时扣午休，超过 9 小时再扣晚餐；休息时长取自考勤组（默认午休60/晚餐30分钟）。
    /// </summary>
    private decimal CalcWorkHours(DateTime clockIn, DateTime clockOut, int? groupId)
    {
        var group = groupId.HasValue ? db.AttendanceGroups.Find(groupId.Value) : null;
        return ComputeWorkHours(clockIn, clockOut, group?.LunchBreakMinutes ?? 60, group?.DinnerBreakMinutes ?? 30);
    }

    /// <summary>
    /// 算某天的实际工时（正班），并处理"休息日自己打卡、没有批准的加班申请就不算工时"这条规则：
    /// 员工只是在自己的休息日/没排班的周末跑来打卡，又没有走加班申请审批，这段时间不能绕开审批流程
    /// 凭空算成正班工时；一旦这天已经有批准的加班（OvertimeHours>0，说明公司认可这天需要上班），
    /// 实际打卡时长就正常按班次时间计入工时。★ 本地打卡、补卡/审批回写都调这一个，保证口径一致。
    /// </summary>
    private async Task<decimal> ComputeDailyWorkHoursAsync(
        AttendanceRecord record, DateOnly workDate, DateTime clockIn, DateTime clockOut, ShiftSchedule? shift, int? groupId)
    {
        var (effectiveClockIn, secondHalfBoundary) = await ResolveEffectiveClockInAsync(record, workDate, clockIn, shift);
        if (record.OvertimeHours <= 0 && await IsNonCompRestDayAsync(db, workDate, shift, groupId))
            return 0;
        var effectiveClockOut = ClampEffectiveClockOut(workDate, clockOut, shift, secondHalfBoundary);
        return CalcWorkHours(effectiveClockIn, effectiveClockOut, groupId);
    }

    /// <summary>
    /// 算"有效上班时间"（供工时计算用）：按班次配置的每一段午间必打卡窗口，查当天有没有打卡落在里面
    /// （没配窗口的班次跳过这步），顺带把每一段的判定结果写回 record.MidCheckResults，
    /// 再交给 <see cref="ClampEffectiveClockIn"/> 统一算出最终的有效上班时间。
    /// </summary>
    private async Task<(DateTime EffectiveClockIn, DateTime? SecondHalfAbsentBoundary)> ResolveEffectiveClockInAsync(
        AttendanceRecord record, DateOnly workDate, DateTime clockIn, ShiftSchedule? shift)
    {
        var windows = shift?.ParseMidCheckWindows() ?? [];
        if (shift is null || windows.Count == 0)
        {
            record.MidCheckResults = null;
            return (ClampEffectiveClockIn(workDate, clockIn, shift, []), null);
        }

        // 这一天所有打卡（不分类型）一次性查出来，再挨个窗口去里面找命中的那次
        var dayPunches = await db.AttendancePunches
            .Where(p => p.UserId == record.UserId
                     && p.PunchTime >= workDate.ToDateTime(TimeOnly.MinValue).AddDays(-1)
                     && p.PunchTime <= workDate.ToDateTime(TimeOnly.MinValue).AddDays(2))
            .Select(p => p.PunchTime)
            .ToListAsync();
        // 这次打卡本身可能刚 Add 但还没 SaveChanges，数据库还查不到，要单独补进去——不然如果正好
        // 是这次打卡本身落在窗口里（比如误判成下班的午间打卡），会查不到自己这一条、误判成没满足窗口。
        // 跟 ZKDeviceSyncService 那边的同类查询保持一致做法。
        dayPunches.AddRange(db.AttendancePunches.Local.Where(p => p.UserId == record.UserId).Select(p => p.PunchTime));

        var results = ResolveMidCheckResults(workDate, shift, windows, dayPunches.Distinct().ToList());
        record.MidCheckResults = results.FormatMidCheckResults();

        var missedEnds = ResolveMissedNonLastWindowEnds(workDate, shift, results);
        var effectiveClockIn = ClampEffectiveClockIn(workDate, clockIn, shift, missedEnds);
        return (effectiveClockIn, ResolveSecondHalfAbsentBoundary(workDate, shift, results));
    }

    /// <summary>按班次配置的每一段午间窗口，从给定的打卡时刻列表里找出每一段命中的那次打卡（没命中就是 null）。</summary>
    public static List<MidCheckWindowResult> ResolveMidCheckResults(
        DateOnly workDate, ShiftSchedule shift, List<(TimeOnly Start, TimeOnly End)> windows, List<DateTime> punchTimes)
    {
        var results = new List<MidCheckWindowResult>();
        foreach (var w in windows)
        {
            var windowStart = ResolveShiftTime(workDate, w.Start, shift);
            var windowEnd   = ResolveShiftTime(workDate, w.End, shift);
            var hit = punchTimes.Where(t => t >= windowStart && t <= windowEnd)
                .OrderBy(t => t).Cast<DateTime?>().FirstOrDefault();
            results.Add(new MidCheckWindowResult(w.Start, w.End, hit.HasValue ? TimeOnly.FromDateTime(hit.Value) : null));
        }
        return results;
    }

    /// <summary>
    /// 把班次里的某个"钟点"（比如午间必打卡的开始/结束时间）换算成 workDate 当天的具体时刻。
    /// 跨天班次（夜班）要判断这个钟点是在午夜前还是午夜后：比应上班时刻还早，说明已经跨过午夜，
    /// 落在 workDate 的第二天（比如 22:00 上班的夜班，配了 02:00~03:00 的窗口，02:00 早于 22:00，
    /// 就该顺延到第二天凌晨，不能按 workDate 当天的 02:00 算，那样会比上班时间还早，完全不对）。
    /// </summary>
    public static DateTime ResolveShiftTime(DateOnly workDate, TimeOnly time, ShiftSchedule shift)
    {
        var dt = workDate.ToDateTime(time);
        if (shift.IsCrossDay && time < shift.WorkStartTime) dt = dt.AddDays(1);
        return dt;
    }

    /// <summary>
    /// 考勤机同步时，一次"不是上班、也不是刚打完上班卡没多久"的打卡，够不够资格被当成"下班候选"——
    /// 判断依据不是"是否落在配置的午间必打卡窗口内"（这条路以前试过，效果不可靠：员工午休回来
    /// 打卡只要没精确落进窗口，就会被误判成下班，在真正下班打卡之前账号上会显示一段"早退"，
    /// 等真正下班打卡后才被纠正回来，用户能看到这个中间态、会以为系统出错），改成看"离排班的
    /// 应下班时间还有多久"——只有到了应下班时间前 <see cref="ClockOutEligibleHoursBeforeEnd"/> 小时
    /// 以内，才算下班候选；这之前的打卡（不管落不落在午间必打卡窗口里）一律当"午间打卡"处理，
    /// 不碰上下班时间和状态。没排班时不知道应下班时间，只能按老办法直接当下班。
    /// 代价：如果员工真的提前很多（超过这个小时数）就走了、之后再也没打卡，当天会显示"未打卡"
    /// 而不是"早退"——比起员工每天午休回来都被误判"早退"，这个取舍更合理。
    /// </summary>
    public const int ClockOutEligibleHoursBeforeEnd = 2;

    public static bool IsEligibleClockOutCandidate(DateTime time, DateOnly workDate, ShiftSchedule? shift)
    {
        if (shift is null) return true;
        var scheduledEnd = workDate.ToDateTime(shift.WorkEndTime);
        if (shift.IsCrossDay) scheduledEnd = scheduledEnd.AddDays(1);
        return time >= scheduledEnd.AddHours(-ClockOutEligibleHoursBeforeEnd);
    }

    /// <summary>
    /// 算"有效上班时间"（供工时计算用），规则叠加，谁把时间往后推得更多就用谁：
    /// 1) 不能靠提前打卡多算钱：有效上班时间不早于排班的应上班时间；
    /// 2) 班次配了午间必打卡窗口的，每一段独立判定：当天没有任何打卡落在某一段窗口内，
    ///    视为"这一段之前没上班"，从这段窗口的结束时间起算；配了多段、缺了不止一段的，
    ///    取"影响最大"（结束时间最晚）的那一段，不会因为缺了好几段就反复往后推、越推越多。
    /// ★ 全系统唯一口径：本地打卡、钉钉同步、补卡回写都调这一个，保证结果一致。
    /// </summary>
    public static DateTime ClampEffectiveClockIn(DateOnly workDate, DateTime clockIn, ShiftSchedule? shift, IReadOnlyList<DateTime> missedWindowEnds)
    {
        var effective = clockIn;

        if (shift is not null)
        {
            var scheduledStart = workDate.ToDateTime(shift.WorkStartTime);
            if (scheduledStart > effective) effective = scheduledStart;
        }

        foreach (var windowEnd in missedWindowEnds)
            if (windowEnd > effective) effective = windowEnd;

        return effective;
    }

    /// <summary>
    /// 算"有效下班时间"（供工时计算用）：不能靠晚走多算钱，有效下班时间不晚于排班的应下班时间
    /// （跨天班次顺延到第二天）；提前下班（早退）不受影响，仍按实际下班时间算，正常反映早退少算的工时。
    /// 加班不再从打卡时间估算，只认「加班申请」审批通过后累加到 OvertimeHours 的时长。
    /// <paramref name="secondHalfAbsentBoundary"/>：配了午间打卡的班次，如果时间最晚的那一段午间窗口
    /// 没打上（见 <see cref="ResolveSecondHalfAbsentBoundary"/>），从这段窗口**开始**时间起到下班就不再
    /// 计入工时（窗口本身就是午休时段，本来就不该算钱；相当于下半个班次不算出勤），不影响
    /// AttendanceStatus，也不发旷工提醒/不计入旷工统计。
    /// ★ 全系统唯一口径：本地打卡、钉钉同步、补卡回写都调这一个，保证结果一致。
    /// </summary>
    public static DateTime ClampEffectiveClockOut(DateOnly workDate, DateTime clockOut, ShiftSchedule? shift, DateTime? secondHalfAbsentBoundary = null)
    {
        if (shift is null) return clockOut;
        var scheduledEnd = workDate.ToDateTime(shift.WorkEndTime);
        if (shift.IsCrossDay) scheduledEnd = scheduledEnd.AddDays(1);
        var effective = scheduledEnd < clockOut ? scheduledEnd : clockOut;
        if (secondHalfAbsentBoundary is { } boundary && boundary < effective) effective = boundary;
        return effective;
    }

    /// <summary>
    /// 配了午间打卡窗口的班次，判断"下半个班次算不算旷工（不计工时）"：只看时间最晚的那一段窗口
    /// （不一定是配置里最后一个，取 WindowEnd 最晚的那个），这段没打上就返回它的**窗口开始时间**，
    /// 供 <see cref="ClampEffectiveClockOut"/> 把有效下班时间收窄到这个点，之后（含这段窗口本身、
    /// 也就是午休时段）到实际下班这段都不算工时。
    /// ★ 这里必须用窗口的开始时间，不能用结束时间——窗口本身就是午休/休息时段，午休从来不算工时，
    /// 如果用结束时间当边界，会把"没打卡证明"的这段休息时间也顺带算成了工时，多算钱。用开始时间
    /// 才能保证从午休开始那一刻起（不管是不是真的在休息、还是没回来上班）都不计入，跟"午休本来就
    /// 不算钱"这条基本规则保持一致（发现于 2026-09-17，之前的实现用了结束时间，是个真实 bug）。
    /// 这段打上了，或者班次没配午间窗口，返回 null（不额外限制）。
    /// 只看最晚这一段，不管前面几段有没有漏打——前面漏打已经由 <see cref="ClampEffectiveClockIn"/> 单独顺延处理了。
    /// </summary>
    public static DateTime? ResolveSecondHalfAbsentBoundary(DateOnly workDate, ShiftSchedule? shift, List<MidCheckWindowResult> results)
    {
        if (shift is null || results.Count == 0) return null;
        var lastWindow = results[ResolveLastWindowIndex(results)];
        return lastWindow.IsSatisfied ? null : ResolveShiftTime(workDate, lastWindow.WindowStart, shift);
    }

    /// <summary>
    /// 算"漏打的、且不是最后一段"窗口的结束时间，供 <see cref="ClampEffectiveClockIn"/> 顺延有效
    /// 上班时间用。★ 必须排除最后一段（跟 <see cref="ResolveSecondHalfAbsentBoundary"/> 用
    /// <see cref="ResolveLastWindowIndex"/> 选出的是同一段）——那一段漏打的后果已经单独由
    /// ResolveSecondHalfAbsentBoundary 处理（下半个班次直接不算工时）。之前这里没排除，导致班次
    /// 只配了一段午间窗口（这一段自然也是"最后一段"）时，漏打这一段会同时触发"上班时间顺延到这段
    /// 结束"和"下班时间收窄到这段结束"两条规则，两边都收缩到同一个点，直接把一整天的工时清零——
    /// 本意只是"下半个班次不算"，结果变成"整天不算"，是个真实的工时计算 bug（发现于 2026-09-17 数据核查）。
    /// </summary>
    public static List<DateTime> ResolveMissedNonLastWindowEnds(DateOnly workDate, ShiftSchedule shift, List<MidCheckWindowResult> results)
    {
        if (results.Count == 0) return [];
        var lastIndex = ResolveLastWindowIndex(results);
        return results.Where((r, i) => i != lastIndex && !r.IsSatisfied)
            .Select(r => ResolveShiftTime(workDate, r.WindowEnd, shift)).ToList();
    }

    /// <summary>挑出"最后一段"窗口在列表里的下标：按结束时间最晚排序，若有多段结束时间刚好相同
    /// （班次配置本身少见但没禁止的情况），再按开始时间最晚的排在最后——两个方法都调这一个，
    /// 保证永远认定同一段是"最后一段"。原来两处各自用不同规则挑"最后一段"（这里按 WindowEnd 值
    /// 相等直接排除所有并列的，上面按 OrderBy(...).Last() 只挑一个），结束时间恰好撞在一起时，
    /// 会导致其中一段漏打的窗口两个函数都不处理、也不影响任何计算，等于被悄悄漏掉。</summary>
    private static int ResolveLastWindowIndex(List<MidCheckWindowResult> results) =>
        Enumerable.Range(0, results.Count)
            .OrderBy(i => results[i].WindowEnd).ThenBy(i => results[i].WindowStart)
            .Last();

    /// <summary>
    /// 把工时数规范成"半小时"为最小单位：不足半小时的零头舍去（1.2→1.0，1.7→1.5，1.5 不变）。
    /// 月度报表里所有对外展示/导出的工时合计都过这一道，保证只会出现整数或 x.5，
    /// 和"每日格子舍去小数"是同一个"不足不计"的口径（工资按工时结算，宁少勿多）。
    /// </summary>
    public static decimal FloorToHalf(decimal hours) => Math.Floor(hours * 2) / 2;

    /// <summary>
    /// 纯计算：由上下班时间 + 午休/晚餐扣时算实际工时（小时）。上班超 6h 扣午休、超 9h 再扣晚餐。
    /// ★ 全系统唯一的工时公式：本地打卡、钉钉同步、补卡回写、月度汇总都调这一个，保证口径一致（工资按工时结算）。
    /// 出口统一按半小时取整（<see cref="FloorToHalf"/>）——之前这里是 2 位小数，跟月度汇总"逐日
    /// 半小时取整再累加"的口径不一致，同一份数据在"我的记录"页会出现日明细 8.37、月合计却按 8.0
    /// 累加，员工自己相加对不上（2026-09-17 复审发现，口径登记表 §2/§5 决定统一到半小时）。
    /// </summary>
    public static decimal ComputeWorkHours(DateTime clockIn, DateTime clockOut, int lunchBreak, int dinnerBreak)
    {
        var rawMinutes = (decimal)(clockOut - clockIn).TotalMinutes;   // 在岗总分钟（夜班下班在第二天也没问题）
        if (rawMinutes <= 0) return 0;
        // 两道阈值判断都要用没扣过的原始在岗分钟数——之前第二道判断用的是已经减掉午休之后的分钟数，
        // 导致原始在岗时长落在"9~10 小时"这个区间时（减完午休正好又跌回 9 小时以内），
        // 晚餐时长会被漏扣，多算了工时。取整放在最后一步，不能提前，否则会影响这两道阈值判断。
        var minutes = rawMinutes;
        if (rawMinutes > 6 * 60) minutes -= lunchBreak;
        if (rawMinutes > 9 * 60) minutes -= dinnerBreak;
        return FloorToHalf(Math.Max(0, minutes / 60));
    }

    /// <summary>
    /// 算请假区间落在某一天里的时长（小时），供提交申请时的预估总时长、审批通过后逐日回写共用
    /// （★ 全系统唯一口径，两处必须调同一个函数才不会算出两个不一样的数字）。
    /// 跟真实工时公式一样扣午休/晚餐，但封顶在 <paramref name="dailyCapHours"/>（这天排的班次的标准
    /// 工时，没排班传公司默认标准工时）——不能直接把"这一天和请假区间的交集"套用工时公式：那个公式
    /// 是给真实上下班打卡时间设计的，套在跨天请假的"整天"区间上，会把一整晚的睡眠时间也当成
    /// "在岗时长"一起扣两道餐时，算出一天 22.5 小时这种荒谬数字（发现于 2026-09-17 代码审查）。
    /// 用标准工时封顶后，请一整天假最多算一天的标准工时，符合"请假时长"这个数字本来的业务含义。
    /// </summary>
    public static decimal ComputeLeaveHoursForDay(
        DateOnly day, DateTime leaveStart, DateTime leaveEnd, int lunchBreak, int dinnerBreak, decimal dailyCapHours)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd   = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var segStart = leaveStart > dayStart ? leaveStart : dayStart;
        var segEnd   = leaveEnd   < dayEnd   ? leaveEnd   : dayEnd;
        if (segEnd <= segStart) return 0;
        var raw = ComputeWorkHours(segStart, segEnd, lunchBreak, dinnerBreak);
        return Math.Min(raw, dailyCapHours);
    }

    /// <summary>
    /// 这一天跟请假区间是不是有真实交集（不管时长多少，哪怕只有几分钟也算有）。
    /// 跟 <see cref="ComputeLeaveHoursForDay"/> 算出来是不是 0 小时是两回事：后者的 0 可能是
    /// "这一天跟请假区间根本没交集"（比如请假结束时间恰好卡在午夜），也可能是"确实有交集，
    /// 但时长不足半小时、被 <see cref="ComputeWorkHours"/> 最后一步取整成了 0"（比如请假 20 分钟）——
    /// 只有前一种才应该跳过这一天不标记"请假"，后一种如果也跳过，会导致"请了假但没打卡"的短时长
    /// 请假被误判成旷工（发现于 2026-09-18 数据核查，是 09-18 那次"无交集跳过"修复自身的边界缺陷）。
    /// </summary>
    public static bool HasLeaveOverlapForDay(DateOnly day, DateTime leaveStart, DateTime leaveEnd)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd   = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var segStart = leaveStart > dayStart ? leaveStart : dayStart;
        var segEnd   = leaveEnd   < dayEnd   ? leaveEnd   : dayEnd;
        return segEnd > segStart;
    }

    /// <summary>这个人这天的"标准工时"：有排班用排的那个班次自己的标准工时，没排班用公司默认标准工时——
    /// 请假半天时用来算"这天最多还能有多少工时额度"，跟 <see cref="ComputeLeaveHoursForDay"/> 的
    /// dailyCapHours 是同一个概念，抽成公共方法避免各个调用点各写一份判断。</summary>
    public static decimal ResolveDailyStandardHours(ShiftSchedule? shift, decimal defaultDailyHours) =>
        shift?.StandardWorkHours ?? defaultDailyHours;

    /// <summary>
    /// 请假当天如果还有真实打卡（半天假、或先打卡后来才补批的假），按"这天标准工时 − 已经批准的
    /// 请假小时数"封顶后结算实际工时——不能超过这个上限，否则会出现"半天假 + 全天工时"这种既算
    /// 请假又重复计酬的情况；如果这天请的是全天假（<paramref name="leaveHours"/> ≥ 标准工时），
    /// 上限自动变成 0，等价于原来"请假当天工时恒为 0"的行为，两种情形用同一个公式覆盖，
    /// 不用分别写"全天/半天"两套判断（2026-09-17 决定支持半天请假，见口径登记表 §4）。
    /// </summary>
    public static decimal ApplyLeaveHoursCap(decimal computedHours, decimal leaveHours, decimal standardHours) =>
        Math.Min(computedHours, Math.Max(0, standardHours - leaveHours));

    /// <summary>把这天的请假小时数折算成"请假天数"：占当天标准工时的比例四舍五入到最近的 0.5 天
    /// （占比 ≥0.75 算 1 天，[0.25, 0.75) 算 0.5 天，&lt;0.25 算 0 天）。供月度汇总的 LeaveDays
    /// 统计用。旧口径是"占比 >0.5 才算 1 天，否则一律算 0.5 天"，导致同样是"半天假"，上午请假
    /// （比如 3.5h/8h=43.75%）算 0.5 天、下午请假（4.5h/8h=56.25%）却算 1 整天——两种半天假因为
    /// 占比刚好卡在 0.5 两侧，天数差一倍；改成就近取整到 0.5 后两者都落在 0.5 天，不再不对称
    /// （2026-09-21）。</summary>
    public static decimal ResolveLeaveDaysFraction(decimal leaveHours, decimal standardHours)
    {
        if (standardHours <= 0) return 0m;
        var ratio = leaveHours / standardHours;
        return ratio >= 0.75m ? 1m : ratio >= 0.25m ? 0.5m : 0m;
    }

    /// <summary>
    /// 批量算某批员工某月的“夜班天数”（不存表，报表读取时实时算）。
    /// 判定：当天实际出勤(打了上班卡)，且满足以下任一：
    ///   ① 当天排的班是夜班(跨天班次 或 班次名含“夜”)；
    ///   ② 无排班时按打卡时间兜底：18 点后上班，或下班跨到了第二天（适配钉钉数据）。
    /// </summary>
    private Task<Dictionary<int, int>> ComputeNightShiftDaysAsync(List<int> userIds, int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);
        return ComputeNightShiftDaysRangeAsync(userIds, start, end);
    }

    /// <summary>算一段时间内（任意起止日期，不一定是自然月）每个人的夜班天数。</summary>
    private async Task<Dictionary<int, int>> ComputeNightShiftDaysRangeAsync(List<int> userIds, DateOnly start, DateOnly end)
    {
        var result = new Dictionary<int, int>();
        if (userIds.Count == 0) return result;

        // 夜班排班的 (用户, 日期) 集合
        var nightAssign = (await db.ShiftAssignments
                .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= start && a.WorkDate <= end
                         && (a.ShiftSchedule.IsCrossDay || a.ShiftSchedule.ShiftName.Contains("夜")))
                .Select(a => new { a.UserId, a.WorkDate })
                .ToListAsync())
            .Select(x => (x.UserId, x.WorkDate)).ToHashSet();

        // 当月“打了上班卡”的日记录
        var recs = await db.AttendanceRecords
            .Where(r => userIds.Contains(r.UserId) && r.WorkDate >= start && r.WorkDate <= end && r.ClockInTime != null)
            .Select(r => new { r.UserId, r.WorkDate, r.ClockInTime, r.ClockOutTime })
            .ToListAsync();

        foreach (var r in recs)
        {
            var isNight = nightAssign.Contains((r.UserId, r.WorkDate));
            if (!isNight && r.ClockInTime is { } ci)
            {
                if (ci.Hour >= 18) isNight = true;                                   // 晚上 18 点后上班
                else if (r.ClockOutTime is { } co && co.Date > ci.Date) isNight = true;  // 下班跨天
            }
            if (isNight) result[r.UserId] = result.GetValueOrDefault(r.UserId) + 1;
        }
        return result;
    }

    /// <summary>
    /// 算一段时间内的应出勤天数（逐天判断）：
    /// ● 调班补班日：哪怕是休息日也算出勤，优先级最高；
    /// ● 法定节假日/公司休息日：不算出勤；
    /// ● 其它日子：按这个人当天排的班次自己配置的休息日判断（三班倒可能休二、三，不一定是标准周末）；
    ///   没排班的日子没法知道具体休息日规则，退一步按标准周末兜底。
    /// 纯内存计算，不查库——holidaysInRange（未按考勤组过滤的这段时间全部假期）和 shiftByDate
    /// （这个人这段时间的排班字典）由调用方（<see cref="GenerateMonthlySummaryAsync"/>）批量查好传进来，
    /// 避免月度汇总重算几百号人时，这里每人各自重复查一遍同一张假期表和排班表。
    /// </summary>
    private static int CountExpectedWorkdays(DateOnly start, DateOnly end, int? groupId,
        List<Holiday> holidaysInRange, Dictionary<DateOnly, ShiftSchedule> shiftByDate)
    {
        var count = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var holiday = holidaysInRange.FirstOrDefault(h => h.HolidayDate == d
                && (h.AttendanceGroupId == null || h.AttendanceGroupId == groupId));
            if (holiday?.HolidayType == HolidayType.CompensatoryWorkDay) { count++; continue; }
            if (holiday?.HolidayType is HolidayType.LegalHoliday or HolidayType.CompanyRestDay) continue;

            var isRestDay = IsShiftWeeklyRestDay(d, shiftByDate.TryGetValue(d, out var shift) ? shift : null);
            if (!isRestDay) count++;
        }
        return count;
    }

    /// <summary>按部门/考勤组圈出在职员工的编号列表。</summary>
    private async Task<List<int>> BuildUserIdQueryAsync(int? deptId, int? groupId, HashSet<int>? deptIds = null)
    {
        var q = db.Users.Where(u => u.IsActive).AsQueryable();
        if (deptId.HasValue)  q = q.Where(u => u.DepartmentId == deptId.Value);
        if (groupId.HasValue) q = q.Where(u => u.AttendanceGroupId == groupId.Value);
        // 分公司管理员范围过滤：deptIds 是"自己范围内的部门 id 全集"（含下级部门），跟上面 deptId 的
        // 单值精确匹配是两码事——deptId 是页面自己选的筛选条件，deptIds 是登录者身份带来的强制范围
        if (deptIds is not null) q = q.Where(u => u.DepartmentId != null && deptIds.Contains(u.DepartmentId.Value));
        return await q.Select(u => u.Id).ToListAsync();
    }

    /// <summary>把“考勤日记录”实体转成给页面用的展示对象(DTO)。</summary>
    private static AttendanceRecordDto ToDto(AttendanceRecord r) => new()
    {
        Id               = r.Id,
        UserId           = r.UserId,
        EmployeeNo       = r.User?.EmployeeNo,
        RealName         = r.User?.RealName,
        DeptName         = r.User?.Department?.DeptName,
        WorkDate         = r.WorkDate,
        ClockInTime      = r.ClockInTime,
        ClockOutTime     = r.ClockOutTime,
        MidCheckHits     = r.MidCheckResults.ParseMidCheckResults(),
        AttendanceStatus = r.AttendanceStatus,
        StatusText       = StatusText(r.AttendanceStatus),
        StatusCssClass   = StatusCss(r.AttendanceStatus),
        LateMinutes      = r.LateMinutes,
        EarlyLeaveMinutes = r.EarlyLeaveMinutes,
        ActualWorkHours  = r.ActualWorkHours,
        OvertimeHours    = r.OvertimeHours,
        LeaveHours       = r.LeaveHours,
        IsHoliday        = r.IsHoliday,
        ApprovalNote     = r.ApprovalNote,
        LocationAbnormal     = r.LocationAbnormal,
        LocationAbnormalNote = r.LocationAbnormalNote
    };

    /// <summary>把“月度汇总”实体转成展示对象(DTO)。</summary>
    private static MonthlySummaryDto MapSummary(MonthlyAttendanceSummary s,
        List<AttendanceRecordDto> daily) => new()
    {
        UserId             = s.UserId,
        EmployeeNo         = s.User.EmployeeNo,
        RealName           = s.User.RealName,
        DeptName           = s.User.Department?.DeptName,
        Position           = s.User.Position,
        Year               = s.Year,
        Month              = s.Month,
        ExpectedWorkdays   = s.ExpectedWorkdays,
        ActualWorkdays     = s.ActualWorkdays,
        LateCount          = s.LateCount,
        EarlyLeaveCount    = s.EarlyLeaveCount,
        AbsentDays         = s.AbsentDays,
        NotPunchedCount    = s.NotPunchedCount,
        LeaveDays          = s.LeaveDays,
        TotalOvertimeHours = s.TotalOvertimeHours,
        TotalWorkHours     = s.TotalWorkHours,
        ApprovedCount      = s.ApprovedCount,
        DailyRecords       = daily
    };

    /// <summary>把考勤状态翻译成中文（供页面/报表显示）。</summary>
    internal static string StatusText(AttendanceStatus s) => s switch
    {
        AttendanceStatus.Normal     => "正常",
        AttendanceStatus.Late       => "迟到",
        AttendanceStatus.EarlyLeave => "早退",
        AttendanceStatus.Absent     => "旷工",
        AttendanceStatus.Holiday    => "休假",
        AttendanceStatus.OnLeave    => "请假",
        AttendanceStatus.Overtime   => "加班",
        AttendanceStatus.NotPunched => "未打卡",
        AttendanceStatus.BusinessTrip => "出差",
        _                           => "未知"
    };

    /// <summary>
    /// Haversine 公式：根据两点的经纬度，算出它们在地球表面的直线距离（米）。
    /// 用于定位打卡时判断“离打卡点多远”，钉钉打卡定位比对也复用这个公式。
    /// </summary>
    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000; // 地球平均半径（米）
        var dLat = (lat2 - lat1) * Math.PI / 180;   // 纬度差（弧度）
        var dLon = (lon2 - lon1) * Math.PI / 180;   // 经度差（弧度）
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>把考勤状态映射成前端颜色样式（绿=正常、橙=迟到早退、红=旷工缺卡等）。</summary>
    private static string StatusCss(AttendanceStatus s) => s switch
    {
        AttendanceStatus.Normal     => "f-color-green",
        AttendanceStatus.Late       => "f-color-orange",
        AttendanceStatus.EarlyLeave => "f-color-orange",
        AttendanceStatus.Absent     => "f-color-red",
        AttendanceStatus.NotPunched => "f-color-red",
        AttendanceStatus.OnLeave    => "f-color-blue",
        AttendanceStatus.Holiday    => "f-color-gray",
        AttendanceStatus.BusinessTrip => "f-color-blue",
        _                           => ""
    };
}
