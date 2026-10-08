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
        if (approval.ApprovalType == ApprovalType.PunchReplenishment && approval.PunchDate.HasValue
            && approval.PunchTime.HasValue)
            await ApplyApprovedPunchReplenishmentAsync(approval, touchedMonths);
        // ── 加班 ──：加班时长完全以审批单为准，不从打卡时间估算；累加到当天的加班时长上
        // （同一天可能有多张已批准的加班单，所以是加，不是覆盖）
        else if (approval.ApprovalType == ApprovalType.Overtime && approval.OvertimeStartTime.HasValue
                 && approval.OvertimeDurationHours is > 0)
            await ApplyApprovedOvertimeAsync(approval, touchedMonths);
        // ── 请假 ──
        else if (approval.ApprovalType == ApprovalType.Leave && approval.LeaveStartTime.HasValue)
            await ApplyApprovedLeaveAsync(approval, touchedMonths);
        // ── 出差 ──：出差期间不用打卡，逐天置为「出差」并按全勤记工时（工资按工时结算，不能漏记）
        else if (approval.ApprovalType == ApprovalType.BusinessTrip && approval.BusinessTripStartTime.HasValue)
            await ApplyApprovedBusinessTripAsync(approval, touchedMonths);

        await db.SaveChangesAsync();

        // 同步刷新受影响月份的月度汇总，不用再等人工点"重新生成"（GenerateMonthlySummaryAsync 本身是幂等的）
        foreach (var (y, m) in touchedMonths)
            await GenerateMonthlySummaryAsync(y, m, [approval.ApplicantUserId]);
    }

    /// <summary>补卡审批通过后回写：补上班/下班时间，重算当天工时。</summary>
    private async Task ApplyApprovedPunchReplenishmentAsync(ApprovalRequest approval, HashSet<(int Year, int Month)> touchedMonths)
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
        if (approval.PunchType == PunchType.ClockOut)
        {
            // 补的下班卡如果比上班卡还早，或者夜班（跨天班次）填的是"班次结束的那个凌晨时间"，说明下班在第二天：
            // 顺延一天。申请只有"日期 + 时间"，没法直接表达跨天，不顺延的话夜班漏打一次下班卡整晚工时都会丢
            var shiftForPunch = (await GetShiftAssignmentAsync(approval.ApplicantUserId, approval.PunchDate.Value))?.ShiftSchedule;
            punchDt = ResolvePunchReplenishmentClockOut(approval.PunchDate.Value, approval.PunchTime.Value, record.ClockInTime, shiftForPunch);
        }
        // 提交时已经按同一套顺延规则查过一次（SubmitApprovalAsync），这里是审批时的兜底：提交之后、
        // 审批之前员工又补上了上班卡，顺延结果可能从"过去"变成"未来"（2026-09-29 第 12 轮审查发现，
        // M5 只堵了提交那一刻，没堵审批这一刻）。这里直接抛异常，让外层事务整单回滚，不会出现
        // "改了一半"的记录。
        if (punchDt > clock.LocalNow())
            throw new BusinessException(
                $"补卡时间 {punchDt:MM-dd HH:mm} 还没到，现在不能审批通过（夜班下班卡会算到第二天），请到时间后再审批或驳回");
        if (approval.PunchType == PunchType.ClockIn) record.ClockInTime  = punchDt;   // 补上班卡
        else                                          record.ClockOutTime = punchDt;   // 补下班卡
        AppendApprovalNote(record, $"补卡已审批通过（{approval.RequestNo}）");
        record.UpdatedAt    = clock.LocalNow();

        // 补齐上下班两次卡后：重算当天实际工时（工资按工时结算，补完卡必须把工时补准），
        // 并解除“旷工/未打卡”状态（否则人有全天工时却仍被记旷工，工资和出勤对不上）。
        await RecalcWorkHoursAfterManualPunchAsync(record, approval.ApplicantUserId);
        touchedMonths.Add((approval.PunchDate.Value.Year, approval.PunchDate.Value.Month));
    }

    /// <summary>加班审批通过后回写：按审批单时长累加当天加班工时（加班时长只认审批单）。</summary>
    private async Task ApplyApprovedOvertimeAsync(ApprovalRequest approval, HashSet<(int Year, int Month)> touchedMonths)
    {
        var workDate = DateOnly.FromDateTime(approval.OvertimeStartTime.Value);
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
        var otHours = approval.OvertimeEndTime.HasValue
            ? ComputeWorkHours(approval.OvertimeStartTime.Value, approval.OvertimeEndTime.Value)
            : approval.OvertimeDurationHours.Value;
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
    private async Task ApplyApprovedLeaveAsync(ApprovalRequest approval, HashSet<(int Year, int Month)> touchedMonths)
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
            if (!HasLeaveOverlapForDay(d, approval.LeaveStartTime.Value, leaveEnd, leaveShift)) continue;
            // 休息日不算请假（婚假/产假/丧假除外）：不新建记录、不标"请假"、不覆盖原来的休假状态
            if (skipNonWorkdays && IsShiftWeeklyRestDay(d, leaveShift)) continue;

            var dailyCap = leaveShift?.StandardWorkHours ?? defaultDailyHours;
            var leaveHoursToday = ComputeLeaveHoursForDay(d, approval.LeaveStartTime.Value, leaveEnd, dailyCap, leaveShift);

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

    /// <summary>出差审批通过后回写：区间内逐天置为出差并按全勤记工时（休息日不算）。</summary>
    private async Task ApplyApprovedBusinessTripAsync(ApprovalRequest approval, HashSet<(int Year, int Month)> touchedMonths)
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
            record.ActualWorkHours = record.AttendanceStatus == AttendanceStatus.OnLeave
                ? ApplyLeaveHoursCap(computedHours, record.LeaveHours, ResolveDailyStandardHours(shift, appOptions.Value.DefaultDailyWorkHours))
                : computedHours;
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
