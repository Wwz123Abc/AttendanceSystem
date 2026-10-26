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

/// <summary>审批通过后回写考勤、管理员手动补卡（<see cref="AttendanceService"/> 的一部分）。</summary>
public partial class AttendanceService
{
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
        if (approval.ApprovalType == ApprovalType.PunchReplenishment && approval.PunchDate is { } punchDate
            && approval.PunchTime is { } punchTime)
            await ApplyApprovedPunchReplenishmentAsync(approval, punchDate, punchTime, touchedMonths);
        // ── 加班 ──：加班时长完全以审批单为准，不从打卡时间估算；累加到当天的加班时长上
        // （同一天可能有多张已批准的加班单，所以是加，不是覆盖）
        else if (approval.ApprovalType == ApprovalType.Overtime && approval.OvertimeStartTime is { } otStart
                 && approval.OvertimeDurationHours is { } otDuration and > 0)
            await ApplyApprovedOvertimeAsync(approval, otStart, otDuration, touchedMonths);
        // ── 请假 ──
        else if (approval.ApprovalType == ApprovalType.Leave && approval.LeaveStartTime is { } leaveStart)
            await ApplyApprovedLeaveAsync(approval, leaveStart, touchedMonths);
        // ── 出差 ──：出差期间不用打卡，逐天置为「出差」并按全勤记工时（工资按工时结算，不能漏记）
        else if (approval.ApprovalType == ApprovalType.BusinessTrip && approval.BusinessTripStartTime is { } tripStart)
            await ApplyApprovedBusinessTripAsync(approval, tripStart, touchedMonths);
        // ── 转班 ──：把申请人这几天的排班改成目标班次（没有排班的日子新建），改完下面重算这几天的正班工时和加班
        else if (approval.ApprovalType == ApprovalType.ShiftChange && approval.ShiftChangeStartDate is { } scStart
                 && approval.ShiftChangeEndDate is { } scEnd && approval.ShiftChangeTargetShiftId is { } scShiftId)
            await ApplyApprovedShiftChangeAsync(approval, scStart, scEnd, scShiftId, touchedMonths);

        await db.SaveChangesAsync();

        // 转班之后：这几天已经有的打卡、加班单要按新班次重新算（跟排班页保存排班后做的是同一件事）
        if (approval.ApprovalType == ApprovalType.ShiftChange && approval.ShiftChangeStartDate is { } rs && approval.ShiftChangeEndDate is { } re)
        {
            var ids = new[] { approval.ApplicantUserId };
            // 先按新班次把这几天"有上下班卡"的记录重新判一遍：工时、迟到/早退分钟数和状态（按旧班次判出的"迟到 690 分钟"
            // 这类结果不能留着继续算进月度汇总，2026-10-26 审查）；请假/出差/节假日的状态不受影响（见该方法里的保护）
            var punched = await db.AttendanceRecords
                .Where(r => r.UserId == approval.ApplicantUserId && r.WorkDate >= rs && r.WorkDate <= re && r.ClockInTime != null && r.ClockOutTime != null)
                .ToListAsync();
            foreach (var rec in punched)
            {
                await RecalcWorkHoursAfterManualPunchAsync(rec, approval.ApplicantUserId);
                AppendApprovalNote(rec, $"转班审批通过（{approval.RequestNo}），迟到/早退/工时已按新班次重判");
                rec.UpdatedAt = clock.LocalNow();
            }
            await db.SaveChangesAsync();
            await RecalcHoursAfterScheduleAsync(ids, rs, re);
            await ReconcileOvertimeAfterScheduleAsync(ids, rs, re);
        }

        // 同步刷新受影响月份的月度汇总，不用再等人工点"重新生成"（GenerateMonthlySummaryAsync 本身是幂等的）
        foreach (var (y, m) in touchedMonths)
            await RefreshMonthlySummarySafelyAsync(y, m, approval.ApplicantUserId);
    }

    /// <summary>
    /// 补卡审批通过后回写。规则见 <see cref="PunchReplenishmentRules"/>：
    /// ① 上班卡只有比现有上班时间更早才写入，下班卡只有比现有下班时间更晚才写入——否则这张补卡"不生效"，
    ///    已有的打卡原样保留，只在备注里记一笔（审批本身已经通过，不报错）；
    /// ② 午间卡只新增一条午间打卡记录，不动上下班时间，然后重新算午间命中和当天工时；条件不满足时报错、不能通过；
    /// ③ 写入前再校验一遍：时间还没到的（夜班下班卡顺延到第二天）不能通过，上下班卡不能越过前一天的下班/下一个上班。
    /// </summary>
    private async Task ApplyApprovedPunchReplenishmentAsync(ApprovalRequest approval, DateOnly punchDate, TimeOnly punchTime, HashSet<(int Year, int Month)> touchedMonths)
    {
        var type = approval.PunchType ?? PunchType.ClockOut;   // 提交时必填；老数据没有类型的按下班处理（原行为）
        var ctx = await PunchReplenishmentRules.LoadAsync(db, approval.ApplicantUserId, punchDate);
        var punchDt = PunchReplenishmentRules.ResolveTime(type, punchDate, punchTime, ctx.ClockIn, ctx.Shift);
        // 提交时已经按同一套规则查过一次，这里是审批时的兜底：提交之后、审批之前现状可能变了（员工又补上了上班卡、
        // 顺延结果从"过去"变成"未来"……）。直接抛异常，让外层事务整单回滚，不会出现"改了一半"的记录。
        if (punchDt > clock.LocalNow())
            throw new BusinessException(
                $"补卡时间 {punchDt:MM-dd HH:mm} 还没到，现在不能审批通过（夜班下班卡会算到第二天），请到时间后再审批或驳回");

        var problem = PunchReplenishmentRules.FindProblem(type, punchDate, punchDt, ctx);
        var record = await db.AttendanceRecords
            .FirstOrDefaultAsync(r => r.UserId == approval.ApplicantUserId && r.WorkDate == punchDate);

        if (type == PunchType.MidCheck)
        {
            if (problem is not null || record is null) throw new BusinessException("午间补卡不能通过：" + (problem ?? "这天没有考勤记录"));
            if (!await db.AttendancePunches.AnyAsync(p => p.UserId == approval.ApplicantUserId && p.PunchType == PunchType.MidCheck && p.PunchTime == punchDt))
                db.AttendancePunches.Add(new AttendancePunch
                {
                    UserId = approval.ApplicantUserId, PunchTime = punchDt, PunchType = PunchType.MidCheck, IsValid = true,
                    DeviceInfo = $"补卡审批:{approval.RequestNo}", CreatedAt = clock.LocalNow()
                });
            AppendApprovalNote(record, $"午间补卡已审批通过（{approval.RequestNo}）{punchDt:HH:mm}");
            record.UpdatedAt = clock.LocalNow();
            // 午间命中要立刻刷新（只有上班卡、还没下班卡时下面的重算会直接返回，不会顺带更新）
            record.MidCheckResults = (await LoadMidCheckResultsAsync(approval.ApplicantUserId, punchDate, ctx.Shift))?.FormatMidCheckResults();
            await RecalcWorkHoursAfterManualPunchAsync(record, approval.ApplicantUserId);
            touchedMonths.Add((punchDate.Year, punchDate.Month));
            return;
        }

        if (problem is not null)
        {
            // 不生效：不改上下班时间，只在备注里留一笔，免得有人以为补卡生效了
            if (record is not null)
            {
                AppendApprovalNote(record, $"补{PunchReplenishmentRules.TypeText(type)}卡已审批通过（{approval.RequestNo}）但未生效：{problem}");
                record.UpdatedAt = clock.LocalNow();
            }
            return;
        }

        if (record is null)
        {
            // 当天完全没有记录也要新建一条：审批都通过了，补卡不能被静默忽略
            record = new AttendanceRecord { UserId = approval.ApplicantUserId, WorkDate = punchDate };
            db.AttendanceRecords.Add(record);
        }
        if (type == PunchType.ClockIn) record.ClockInTime  = punchDt;   // 补上班卡（已确认比现有的更早）
        else                           record.ClockOutTime = punchDt;   // 补下班卡（已确认比现有的更晚）
        AppendApprovalNote(record, $"补卡已审批通过（{approval.RequestNo}）");
        record.UpdatedAt    = clock.LocalNow();

        // 补齐上下班两次卡后：重算当天实际工时（工资按工时结算，补完卡必须把工时补准），
        // 并解除“旷工/未打卡”状态（否则人有全天工时却仍被记旷工，工资和出勤对不上）。
        await RecalcWorkHoursAfterManualPunchAsync(record, approval.ApplicantUserId);
        touchedMonths.Add((punchDate.Year, punchDate.Month));
    }

    /// <summary>加班审批通过后回写：按审批单时长累加当天加班工时（加班时长只认审批单）。</summary>
    /// <summary>
    /// 加班单该记到哪个考勤日。默认是加班开始时间所在的日历日；但夜班过了零点之后接着干的加班（比如晚班 20:30–次日 05:30，
    /// 次日 06:30–09:00 的加班）是前一个考勤日那个夜班的延续，要记到前一天，不能记到"开始时间那个日历日"——
    /// 以前一律记到日历日，夜班凌晨的加班全被记到后一天，后一天出现没有打卡的空记录、前一天缺加班
    /// （2026-10-20 反馈赵子祥 10/19 夜班的加班被记到 10/20，查出 9/27 起共 482 张单受影响）。
    /// 判断：开始时间在上午；前一天排的是跨天班次，且开始时间还在它应下班时间后的续接窗口内
    /// （<see cref="NightShiftCarryOverHours"/> 小时，跟夜班下班卡归属用同一个口径）；今天自己的班次如果已经开始了
    /// （开始时间不早于今天班次的上班时间）则算今天的。
    /// </summary>
    public async Task<DateOnly> ResolveOvertimeWorkDateAsync(int userId, DateTime otStart)
    {
        var calendarDay = DateOnly.FromDateTime(otStart);
        if (otStart.Hour >= 12) return calendarDay;   // 上午才可能是夜班的延续，省掉两次查库
        var yShift = (await GetShiftAssignmentAsync(userId, calendarDay.AddDays(-1)))?.ShiftSchedule;
        var todayShift = (await GetShiftAssignmentAsync(userId, calendarDay))?.ShiftSchedule;
        return ResolveOvertimeWorkDate(otStart, yShift, todayShift);
    }

    /// <summary><see cref="ResolveOvertimeWorkDateAsync"/> 的纯计算版本（批量对账时班次已经预加载好，不用再逐张单查库）。</summary>
    internal static DateOnly ResolveOvertimeWorkDate(DateTime otStart, ShiftSchedule? yShift, ShiftSchedule? todayShift)
    {
        var calendarDay = DateOnly.FromDateTime(otStart);
        if (otStart.Hour >= 12) return calendarDay;
        var yesterday = calendarDay.AddDays(-1);
        if (yShift is not { IsCrossDay: true } || !IsWithinNightCarryOver(yesterday, yShift, otStart)) return calendarDay;
        if (todayShift is { IsCrossDay: false } && TimeOnly.FromDateTime(otStart) >= todayShift.WorkStartTime) return calendarDay;
        return yesterday;
    }

    private async Task ApplyApprovedOvertimeAsync(ApprovalRequest approval, DateTime otStart, decimal otDuration, HashSet<(int Year, int Month)> touchedMonths)
    {
        var workDate = await ResolveOvertimeWorkDateAsync(approval.ApplicantUserId, otStart);
        var record = await db.AttendanceRecords
            .FirstOrDefaultAsync(r => r.UserId == approval.ApplicantUserId && r.WorkDate == workDate);
        if (record is null)
        {
            record = new AttendanceRecord { UserId = approval.ApplicantUserId, WorkDate = workDate };
            db.AttendanceRecords.Add(record);
        }

        // 加班时长按"申请的起止时间"重新算一遍再记：超过 6 小时扣午休、超过 9 小时再扣晚餐（跟提交时同一个函数）。
        // 不直接用单子上存的时长，是因为改规则之前提交、还没批的老单子存的是不扣饭点的总长度；
        // 算完顺手把单子上的时长也改成实际记入的数，审批列表/记录里看到的和考勤上记的一致
        var otGroupId = await db.Users.Where(u => u.Id == approval.ApplicantUserId).Select(u => u.AttendanceGroupId).FirstOrDefaultAsync();
        // 工作日的加班只记班次上下班时间之外的部分（班次没排就用考勤组最常用的日班兜底），不能和正班重复计算
        decimal otHours;
        if (approval.OvertimeEndTime.HasValue)
        {
            var (otShifts, otFallbackDays) = await LoadOvertimeShiftsAsync(approval.ApplicantUserId, DateOnly.FromDateTime(otStart), otGroupId);
            otHours = ComputeOvertimeHoursOutsideShift(otStart, approval.OvertimeEndTime.Value, otShifts, otGroupId, otFallbackDays);
        }
        else otHours = otDuration;
        approval.OvertimeDurationHours = otHours;

        record.OvertimeHours += otHours;
        AppendApprovalNote(record, $"加班已审批通过（{approval.RequestNo}），{otHours:0.##} 小时");
        record.UpdatedAt      = clock.LocalNow();

        // 员工可能是先自己打卡上班、事后才补的加班申请。休息日的正班工时不管有没有批加班都是 0
        // （加班时长只认审批单，见 ComputeDailyWorkHoursAsync），这里重算一遍主要是让状态/迟到早退等跟着当前规则走；
        // 如果这天其实是工作日（加班单批在工作日），已有的打卡工时也一并刷新。
        await RecalcWorkHoursAfterManualPunchAsync(record, approval.ApplicantUserId);
        touchedMonths.Add((workDate.Year, workDate.Month));
    }

    /// <summary>请假审批通过后回写：区间内逐天置为请假，累加请假小时，必要时重算有真实打卡那天的工时。</summary>
    private async Task ApplyApprovedLeaveAsync(ApprovalRequest approval, DateTime leaveStart, HashSet<(int Year, int Month)> touchedMonths)
    {
        var sd = DateOnly.FromDateTime(leaveStart);
        var leaveEnd = approval.LeaveEndTime ?? leaveStart;
        var ed = DateOnly.FromDateTime(leaveEnd);

        // 先把请假区间内已经存在的考勤记录一次性整批查出来，按"日期"放进一个字典。
        // 原来的写法是下面 for 循环里每天单独查一次数据库——请十天假就要查十次数据库，
        // 现在改成先查一次、缓存到内存里，下面循环直接从内存里找，结果完全一样，只是数据库跑得更少更快。
        var recordsInRange = await db.AttendanceRecords
            .Where(r => r.UserId == approval.ApplicantUserId && r.WorkDate >= sd && r.WorkDate <= ed)
            .ToDictionaryAsync(r => r.WorkDate);

        // 这天排的班次的标准工时（没排班就用公司默认标准工时）给 ComputeLeaveHoursForDay 封顶用，
        // 只查一次，下面循环里每天复用。
        var leaveShiftsInRange = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => a.UserId == approval.ApplicantUserId && a.WorkDate >= sd && a.WorkDate <= ed)
                .ToListAsync())
            .ToDictionary(a => a.WorkDate, a => a.ShiftSchedule);
        var defaultDailyHours = appOptions.Value.DefaultDailyWorkHours;
        // 事假/病假/年假/调休：休息日不算请假
        var skipNonWorkdays = !LeaveCountsNaturalDays(approval.LeaveType);

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
            leaveShiftsInRange.TryGetValue(d, out var leaveShift);
            if (!HasLeaveOverlapForDay(d, leaveStart, leaveEnd, leaveShift)) continue;
            // 休息日不算请假（婚假/产假/丧假除外）：不新建记录、不标"请假"、不覆盖原来的休假状态
            if (skipNonWorkdays && IsShiftWeeklyRestDay(d, leaveShift)) continue;

            var dailyCap = leaveShift?.StandardWorkHours ?? defaultDailyHours;
            var leaveHoursToday = ComputeLeaveHoursForDay(d, leaveStart, leaveEnd, dailyCap, leaveShift);   // 考勤组自定义饭点只用于加班申请的时长核对；正班工时、请假时长一律按默认饭点算（2026-10-13 用户确认）

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
            // 计时工不走这条估算：只有上班卡、没有下班卡的当天工时就是 0（跟计时工的统一规则一致）
            if (record.ClockInTime is { } workedCi && record.ClockOutTime is null
                && !await IsHourlyOnAsync(await db.Users.Where(u => u.Id == approval.ApplicantUserId).Select(u => u.AttendanceGroupId).FirstOrDefaultAsync(), d))
            {
                var dayStart      = d.ToDateTime(TimeOnly.MinValue);
                var leaveSegStart = leaveStart > dayStart ? leaveStart : dayStart;
                if (workedCi < leaveSegStart)
                {
                    var estimatedWork = ComputeWorkHours(workedCi, leaveSegStart);
                    record.ActualWorkHours = ApplyLeaveHoursCap(estimatedWork, record.LeaveHours, dailyCap);
                }
            }

            // 备注带上这一天的请假时长，跟加班审批的备注格式一致（"加班已审批通过（单号），9 小时"）
            AppendApprovalNote(record, $"请假审批通过（{approval.RequestNo}），{leaveHoursToday:0.##} 小时");
            record.UpdatedAt        = clock.LocalNow();
            touchedMonths.Add((d.Year, d.Month));
        }
    }

    /// <summary>
    /// 转班审批通过后回写：把申请人 [开始日期, 结束日期] 每天的排班改成目标班次，没有排班的日子新建一条；
    /// 入职日期之前的日子不排。目标班次已经被停用/删除就什么都不改（回写时再校验一次，不留悬空排班）。
    /// 考勤记录、加班单的重算在 <see cref="UpdateAttendanceAfterApprovalAsync"/> 里统一做。
    /// </summary>
    private async Task ApplyApprovedShiftChangeAsync(ApprovalRequest approval, DateOnly start, DateOnly end, int shiftId, HashSet<(int Year, int Month)> touchedMonths)
    {
        var applicant = await db.Users.Where(u => u.Id == approval.ApplicantUserId).Select(u => new { u.HireDate }).FirstOrDefaultAsync();
        if (applicant is null || !await db.ShiftSchedules.AnyAsync(s => s.Id == shiftId && s.IsActive)) return;
        var existing = await db.ShiftAssignments
            .Where(a => a.UserId == approval.ApplicantUserId && a.WorkDate >= start && a.WorkDate <= end)
            .ToDictionaryAsync(a => a.WorkDate);
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (applicant.HireDate is { } hire && hire > d) continue;
            if (existing.TryGetValue(d, out var a)) a.ShiftScheduleId = shiftId;
            else db.ShiftAssignments.Add(new ShiftAssignment { UserId = approval.ApplicantUserId, ShiftScheduleId = shiftId, WorkDate = d, IsAutoAssigned = false, CreatedAt = clock.LocalNow() });
            touchedMonths.Add((d.Year, d.Month));
        }
    }

    /// <summary>出差审批通过后回写：区间内逐天置为出差并按全勤记工时（休息日不算）。</summary>
    private async Task ApplyApprovedBusinessTripAsync(ApprovalRequest approval, DateTime tripStart, HashSet<(int Year, int Month)> touchedMonths)
    {
        var sd = DateOnly.FromDateTime(tripStart);
        var ed = DateOnly.FromDateTime(approval.BusinessTripEndTime ?? tripStart);
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
            // 休息日不算出差：不覆盖原来的休假状态，也不白给一天标准工时和出勤（2026-09-24 用户确认）
            shiftsInRange.TryGetValue(d, out var tripShiftAssign);
            if (IsShiftWeeklyRestDay(d, tripShiftAssign?.ShiftSchedule)) continue;

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
            record.UpdatedAt        = clock.LocalNow();
            touchedMonths.Add((d.Year, d.Month));
        }
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
            throw new BusinessException("补卡备注不能超过 100 个字");

        // 打卡时间必须落在"这个考勤日当天或第二天"（跨天班次/加班过零点的下班在第二天）：
        // 选错年月的话（比如日期是 9 月、时间填成 8 月）会算出几百小时的工时，而且没有任何提示
        foreach (var t in new[] { clockIn, clockOut })
        {
            if (t is null) continue;
            var td = DateOnly.FromDateTime(t.Value);
            if (td < workDate || td > workDate.AddDays(1))
                throw new BusinessException($"打卡时间 {t.Value:yyyy-MM-dd HH:mm} 不在考勤日 {workDate:yyyy-MM-dd} 当天或第二天，请检查日期");
            // 补卡是补一次已经真实发生过的打卡，不能补"还没到"的时间点（员工自助补卡/审批回写都是这个口径）——
            // 不挡的话可以给未来任意一天写打卡、结算工时、刷新那个未来月的月度汇总
            if (t.Value > clock.LocalNow())
                throw new BusinessException($"打卡时间 {t.Value:yyyy-MM-dd HH:mm} 还没到，不能补录未来的打卡");
        }
        if (clockIn.HasValue && clockOut.HasValue && clockOut.Value <= clockIn.Value)
            throw new BusinessException("下班时间必须晚于上班时间（夜班下班在第二天，请把日期选到第二天）");

        var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == workDate);
        var adjustLog = SnapshotBeforeAdjust(record, userId, workDate,
            clockIn.HasValue && clockOut.HasValue ? "上班+下班" : clockIn.HasValue ? "上班" : "下班", operatorName, remark);
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
        record.UpdatedAt    = clock.LocalNow();

        await RecalcWorkHoursAfterManualPunchAsync(record, userId);
        adjustLog.NewClockIn  = record.ClockInTime;
        adjustLog.NewClockOut = record.ClockOutTime;
        db.PunchAdjustLogs.Add(adjustLog);
        await db.SaveChangesAsync();

        // 同步刷新这个月的月度汇总，道理和审批回写那边一样，不用等人工点"重新生成"
        await RefreshMonthlySummarySafelyAsync(workDate.Year, workDate.Month, userId);
    }

    /// <summary>手动补卡之前，把这天考勤记录的样子存下来（撤回时恢复）。这天原来没有记录就只记"没有记录"。</summary>
    private PunchAdjustLog SnapshotBeforeAdjust(AttendanceRecord? record, int userId, DateOnly workDate, string adjustType, string? operatorName, string? remark) => new()
    {
        UserId = userId, WorkDate = workDate, OperatorName = operatorName, AdjustType = adjustType,
        Remark = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim(),
        RecordExisted = record is not null,
        PrevClockIn = record?.ClockInTime, PrevClockOut = record?.ClockOutTime,
        PrevStatus = record?.AttendanceStatus ?? default,
        PrevActualWorkHours = record?.ActualWorkHours ?? 0,
        PrevLateMinutes = record?.LateMinutes ?? 0, PrevEarlyLeaveMinutes = record?.EarlyLeaveMinutes ?? 0,
        PrevMidCheckResults = record?.MidCheckResults,
        CreatedAt = clock.LocalNow()
    };

    /// <summary>
    /// 撤回一次管理员手动补卡，见 <see cref="IAttendanceService.RevertAdminPunchAdjustAsync"/>。
    /// 只恢复补卡动过的那几项（上下班时间、状态、工时、迟到早退、午间命中、补午间卡新增的那条打卡）；
    /// 加班时长、请假等其它信息不动，备注里保留原有内容并追加一句"已撤回"。
    /// </summary>
    public async Task RevertAdminPunchAdjustAsync(int logId, string? operatorName)
    {
        var log = await db.PunchAdjustLogs.FirstOrDefaultAsync(l => l.Id == logId)
                  ?? throw new BusinessException("找不到这条补卡操作记录");
        if (log.RevertedAt is not null) throw new BusinessException("这次补卡已经撤回过了");
        if (await db.PunchAdjustLogs.AnyAsync(l => l.UserId == log.UserId && l.WorkDate == log.WorkDate && l.RevertedAt == null && l.Id > log.Id))
            throw new BusinessException("这天后面还有更晚的一次补卡，请先撤回更晚的那次");

        var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == log.UserId && r.WorkDate == log.WorkDate)
                     ?? throw new BusinessException("这天的考勤记录已经不存在，没有可撤回的内容");
        if (record.ClockInTime != log.NewClockIn || record.ClockOutTime != log.NewClockOut)
            throw new BusinessException("这天的上班/下班时间在这次补卡之后又被别的操作改过，不能直接撤回，请人工处理");

        if (log.AddedMidCheckTime is { } midTime)
        {
            var added = await db.AttendancePunches.Where(p => p.UserId == log.UserId && p.PunchType == PunchType.MidCheck
                && p.PunchTime == midTime && p.DeviceInfo != null && p.DeviceInfo.StartsWith("管理员补卡")).ToListAsync();
            db.AttendancePunches.RemoveRange(added);
        }

        var opText = string.IsNullOrWhiteSpace(operatorName) ? "管理员" : operatorName;
        var deleteRecord = !log.RecordExisted && record.OvertimeHours == 0 && record.LeaveHours == 0;
        if (deleteRecord)
        {
            db.AttendanceRecords.Remove(record);   // 这天的记录就是这次补卡建出来的，撤回就是删掉
        }
        else
        {
            // 补卡之后这天如果才批了请假/出差/节假日，状态、工时、迟到早退是审批流程定的，不能被补卡前存的旧值换掉；
            // 但如果这类状态在补卡之前就有了（补卡前的状态已经是请假等），现在的工时是补卡算出来的，要恢复成补卡前的值
            static bool IsApprovalState(AttendanceStatus s) => s is AttendanceStatus.OnLeave or AttendanceStatus.Holiday or AttendanceStatus.BusinessTrip;
            var approvalAddedAfter = IsApprovalState(record.AttendanceStatus) && !(log.RecordExisted && IsApprovalState(log.PrevStatus));
            record.ClockInTime  = log.PrevClockIn;
            record.ClockOutTime = log.PrevClockOut;
            if (!IsApprovalState(record.AttendanceStatus))
                record.AttendanceStatus = log.RecordExisted ? log.PrevStatus : AttendanceStatus.NotPunched;
            if (!approvalAddedAfter)
            {
                record.ActualWorkHours    = log.PrevActualWorkHours;
                record.LateMinutes        = log.PrevLateMinutes;
                record.EarlyLeaveMinutes  = log.PrevEarlyLeaveMinutes;
            }
        }

        // 删掉补的午间卡、恢复上下班时间、标记"已撤回"一次落库，保证这几件事要么都做、要么都不做；
        // 下面的重算要读到删除之后的打卡，所以必须先落库
        log.RevertedAt = clock.LocalNow();
        log.RevertedBy = operatorName;
        await db.SaveChangesAsync();

        if (!deleteRecord)
        {
            // 补卡之后午间卡可能被别的途径（比如考勤机补传）改动过，这里不能把补卡前存的旧值整套写回去：
            // 午间命中按现在所有打卡重新读，上下班卡都在的话工时/状态也按现在的打卡重新算（2026-10-16 复审）
            var shift = (await GetShiftAssignmentAsync(log.UserId, log.WorkDate))?.ShiftSchedule;
            record.MidCheckResults = (await LoadMidCheckResultsAsync(log.UserId, log.WorkDate, shift))?.FormatMidCheckResults();
            await RecalcWorkHoursAfterManualPunchAsync(record, log.UserId);
            AppendApprovalNote(record, $"管理员手动补卡已撤回（{log.AdjustType}卡，原操作人：{log.OperatorName}，撤回：{opText}）");
            record.UpdatedAt = clock.LocalNow();
            await db.SaveChangesAsync();
        }
        await RefreshMonthlySummarySafelyAsync(log.WorkDate.Year, log.WorkDate.Month, log.UserId);
    }

    /// <summary>
    /// 管理员帮员工补一次卡（上班 / 下班 / 午间）：和员工自己提交"补卡申请"经审批通过后的效果一致，用同一套规则
    /// （<see cref="PunchReplenishmentRules"/>）——上班卡要比现有的更早、下班卡要比现有的更晚、午间卡只新增一条午间打卡
    /// 且必须落在还没命中的窗口里，位置上不能越过前一天的下班卡/下一个上班；区别只是不用审批、立即生效。
    /// 不满足就抛 <see cref="BusinessException"/> 写明原因。管理员确实要改掉已有的上班/下班时间时勾"强制覆盖"
    /// （<paramref name="allowOverwrite"/>）：只跳过"更早/更晚"这一条，其余检查照旧。
    /// 备注里写明操作人和补的是哪种卡，页面下方的操作记录靠它追溯。
    /// </summary>
    public async Task AdminReplenishPunchAsync(int userId, DateOnly workDate, PunchType type, DateTime punchAt, bool allowOverwrite, string? remark, string? operatorName)
    {
        if (!string.IsNullOrWhiteSpace(remark) && remark.Trim().Length > 100)
            throw new BusinessException("补卡备注不能超过 100 个字");
        var td = DateOnly.FromDateTime(punchAt);
        if (td < workDate || td > workDate.AddDays(1))
            throw new BusinessException($"打卡时间 {punchAt:yyyy-MM-dd HH:mm} 不在考勤日 {workDate:yyyy-MM-dd} 当天或第二天，请检查日期");
        if (punchAt > clock.LocalNow())
            throw new BusinessException($"打卡时间 {punchAt:yyyy-MM-dd HH:mm} 还没到，不能补录未来的打卡");

        var ctx = await PunchReplenishmentRules.LoadAsync(db, userId, workDate);
        // 管理员"强制覆盖"只对上班/下班卡有意义
        var problem = PunchReplenishmentRules.FindProblem(type, workDate, punchAt, ctx, allowOverwrite && type != PunchType.MidCheck);
        if (problem is not null) throw new BusinessException(problem);

        var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == workDate);
        var typeText = PunchReplenishmentRules.TypeText(type);
        var adjustLog = SnapshotBeforeAdjust(record, userId, workDate, typeText, operatorName, remark);
        var opText = string.IsNullOrWhiteSpace(operatorName) ? "管理员手动补卡" : $"管理员手动补卡（操作人：{operatorName}）";
        var note = $"{opText}：补{typeText}卡 {punchAt:HH:mm}" + (string.IsNullOrWhiteSpace(remark) ? "" : $"，{remark.Trim()}");

        if (type == PunchType.MidCheck)
        {
            if (record is null) throw new BusinessException("这天没有考勤记录，请先补上班卡");   // 规则已保证有上班卡，这里只是防御
            if (!await db.AttendancePunches.AnyAsync(p => p.UserId == userId && p.PunchType == PunchType.MidCheck && p.PunchTime == punchAt))
                db.AttendancePunches.Add(new AttendancePunch
                {
                    UserId = userId, PunchTime = punchAt, PunchType = PunchType.MidCheck, IsValid = true,
                    DeviceInfo = $"管理员补卡:{operatorName}", CreatedAt = clock.LocalNow()
                });
            adjustLog.AddedMidCheckTime = punchAt;
            adjustLog.NewClockIn = record.ClockInTime; adjustLog.NewClockOut = record.ClockOutTime;   // 补午间卡不动上下班时间
            db.PunchAdjustLogs.Add(adjustLog);   // 操作记录和新打卡必须一起落库，否则中途出错会留下撤回不掉的午间卡
            await db.SaveChangesAsync();   // 先落库，下面重算午间命中要读到这条新打卡
            AppendApprovalNote(record, note);
            record.UpdatedAt = clock.LocalNow();
            record.MidCheckResults = (await LoadMidCheckResultsAsync(userId, workDate, ctx.Shift))?.FormatMidCheckResults();
        }
        else
        {
            if (record is null)
            {
                record = new AttendanceRecord { UserId = userId, WorkDate = workDate };
                db.AttendanceRecords.Add(record);
            }
            if (type == PunchType.ClockIn) record.ClockInTime = punchAt;
            else                           record.ClockOutTime = punchAt;
            AppendApprovalNote(record, note);
            record.UpdatedAt = clock.LocalNow();
        }

        await RecalcWorkHoursAfterManualPunchAsync(record, userId);
        adjustLog.NewClockIn  = record.ClockInTime;
        adjustLog.NewClockOut = record.ClockOutTime;
        if (type != PunchType.MidCheck) db.PunchAdjustLogs.Add(adjustLog);   // 午间卡的操作记录前面已经和新打卡一起存了
        await db.SaveChangesAsync();
        await RefreshMonthlySummarySafelyAsync(workDate.Year, workDate.Month, userId);
    }

    /// <summary>补卡/撤回/审批回写落库之后顺手刷新月度汇总。打卡数据这时已经提交，汇总刷新失败不能让调用方以为"补卡失败"
    /// （管理员会重复补、审批人会重复点）：只记日志，汇总是幂等的，后台任务/下次重新生成会补上（2026-10-16 复审）。</summary>
    private async Task RefreshMonthlySummarySafelyAsync(int year, int month, int userId)
    {
        try { await GenerateMonthlySummaryAsync(year, month, [userId]); }
        catch (Exception ex) { logger.LogError(ex, "考勤数据已保存，但刷新 {Year}-{Month} 月度汇总失败，UserId={UserId}", year, month, userId); }
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
        // 只补了一边卡（上班或下班）：人确实到过岗，不能继续挂"旷工"——不然月度汇总会把这天既记 1 天旷工、
        // 又记 1 天出勤（出勤只看有没有上班卡）。跟后台任务对"只有下班卡"的处理同一口径：改成"未打卡（缺卡）"。
        if (record.AttendanceStatus == AttendanceStatus.Absent
            && (record.ClockInTime.HasValue ^ record.ClockOutTime.HasValue))
            record.AttendanceStatus = AttendanceStatus.NotPunched;
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
            record.ActualWorkHours = await CapWorkHoursForLeaveAsync(record, computedHours, shift, applicant?.AttendanceGroupId);
        }

        // 迟到/早退分钟数和状态都要按补卡后的新时间重新算一遍，不能沿用改之前的旧值——
        // 不然管理员把一条迟到记录的上班时间改准点了，LateMinutes 还留着旧的迟到分钟数、
        // 状态也可能继续显示"迟到"。出差/节假日这两个审批流程/定时任务设置的状态不受影响。
        var isRestDay      = IsNonCompRestDay(record.WorkDate, shift);
        var clockInStatus  = CalcClockInStatus(record.WorkDate, ci, shift, isRestDay, out var lateMin);
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
}
