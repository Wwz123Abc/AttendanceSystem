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
public class AttendanceService(AttendanceDbContext db, IOptions<AppSettingsOptions> appOptions) : IAttendanceService
{
    /// <summary>
    /// 员工打卡（上班/下班）。流程：
    /// 1) 校验是否节假日；2) 若开了定位打卡，校验距离；
    /// 3) 写一条原始打卡流水；4) 取/建当天考勤记录并算出迟到/早退/工时/加班/状态。
    /// </summary>
    public async Task<PunchResponseDto> PunchAsync(int userId, PunchRequestDto request)
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
        // 做法：下班卡优先续上"昨天已打上班卡、还没打下班卡"的记录——但只在昨天排的确实是跨天班次时才续，
        // 避免把员工真的忘记打卡的旧记录（普通白班）误接到今天的下班卡上。
        var workDate         = today;
        AttendanceRecord? openYesterdayRecord = null;
        if (request.PunchType == PunchType.ClockOut)
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

        // 定位打卡校验：如果考勤组开了定位打卡、且配了打卡地点，就要看打卡位置离「任意一个」地点够不够近
        // （多地点是"或"的关系：命中其中一个地点的有效半径内就算通过，不要求同时满足全部地点）
        if (user.AttendanceGroupId.HasValue)
        {
            var group = await db.AttendanceGroups
                .Include(g => g.Locations)
                .FirstOrDefaultAsync(g => g.Id == user.AttendanceGroupId.Value);
            if (group is { EnableLocationPunch: true } && group.Locations.Count > 0)
            {
                if (!request.Latitude.HasValue || !request.Longitude.HasValue)
                    return new PunchResponseDto { Success = false, Message = "该考勤组已启用定位打卡，请允许浏览器获取位置权限后重试" };

                // 算到每个配置地点的距离，取最近的一个来判断
                var nearest = group.Locations
                    .Select(l => new
                    {
                        l.LocationName,
                        l.RadiusMeters,
                        Distance = HaversineMeters(request.Latitude.Value, request.Longitude.Value, l.Latitude, l.Longitude)
                    })
                    .OrderBy(x => x.Distance)
                    .First();

                if (nearest.Distance > nearest.RadiusMeters)   // 离最近的地点都还超出范围
                    return new PunchResponseDto
                    {
                        Success = false,
                        Message = $"打卡位置超出有效范围（距最近的「{nearest.LocationName ?? "打卡点"}」{nearest.Distance:F0} 米，限 {nearest.RadiusMeters} 米内）"
                    };
            }
        }

        // 打卡时间精确到分钟（把秒抹掉）
        var punchTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);

        // 取 workDate 那天的排班，进而拿到班次（用来判断迟到/早退/加班）
        var assignment = await GetShiftAssignmentAsync(userId, workDate);
        var shift      = assignment is not null
            ? await db.ShiftSchedules.FindAsync(assignment.ShiftScheduleId)
            : null;

        // 第 3 步：写一条原始打卡流水（时间仍然是打卡的真实时刻，不受 workDate 影响）
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
            record.ClockInTime = punchTime;
            status = CalcClockInStatus(punchTime, shift, out var lateMin);   // 算是否迟到
            record.AttendanceStatus = status;
            record.LateMinutes      = lateMin;
            lateMinutes             = lateMin > 0 ? lateMin : null;
            message = lateMin > 0 ? $"上班打卡成功，迟到 {lateMin} 分钟" : "上班打卡成功";
        }
        else if (request.PunchType == PunchType.MidCheck)   // ── 午间打卡 ──
        {
            // 打卡流水已经在上面写好了；这里不改上下班时间/状态，工时结算在下班打卡时统一算（见 ResolveEffectiveClockInAsync）
            status  = record.AttendanceStatus;
            message = "午间打卡成功";
        }
        else                                          // ── 下班卡 ──
        {
            record.ClockOutTime = punchTime;
            status = CalcClockOutStatus(workDate, punchTime, shift, out var earlyMin);  // 算是否早退
            if (earlyMin > 0)
            {
                record.EarlyLeaveMinutes = earlyMin;
                if (record.AttendanceStatus == AttendanceStatus.Normal)   // 没迟到才把状态改成早退
                    record.AttendanceStatus = AttendanceStatus.EarlyLeave;
                message = $"下班打卡成功，早退 {earlyMin} 分钟";
            }
            else
            {
                message = "下班打卡成功";
            }

            // 上下班卡都齐了，算实际工时（不早于应上班时间、不晚于应下班时间——早到晚走都不多算钱）。
            // 加班不再从打卡时间估算：只认「加班申请」审批通过后累加的时长，这里不动 OvertimeHours。
            if (record.ClockInTime.HasValue)
            {
                var effectiveClockIn   = await ResolveEffectiveClockInAsync(record, workDate, record.ClockInTime.Value, shift);
                var effectiveClockOut  = ClampEffectiveClockOut(workDate, punchTime, shift);
                record.ActualWorkHours = CalcWorkHours(effectiveClockIn, effectiveClockOut, user.AttendanceGroupId);
            }
        }

        record.UpdatedAt = now;
        await db.SaveChangesAsync();

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
    public async Task<List<AttendanceRecordDto>> GetDeptAttendanceAsync(DeptAttendanceQueryDto q)
    {
        var userIds = await BuildUserIdQueryAsync(q.DepartmentId, q.AttendanceGroupId);   // 先圈出这批人

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
        int? deptId, int? groupId, int year, int month)
    {
        var idQuery = db.Users.AsQueryable();
        if (deptId.HasValue)  idQuery = idQuery.Where(u => u.DepartmentId == deptId.Value);
        if (groupId.HasValue) idQuery = idQuery.Where(u => u.AttendanceGroupId == groupId.Value);
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
                DingTalkUserId  = user.DingTalkUserId,
                NightShiftDays  = night.GetValueOrDefault(user.Id)
            };

            // 标准工时：取这段时间里出现次数最多的那个班次的标准工时（没排过班就留空）
            row.StandardDailyHours = assignByDate.Values
                .GroupBy(a => a.ShiftScheduleId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.First().ShiftSchedule.StandardWorkHours)
                .FirstOrDefault();
            if (assignByDate.Count == 0) row.StandardDailyHours = null;

            decimal totalWork = 0, businessTripHours = 0;
            decimal totalOtHours = 0, weekdayOtHours = 0, restOtHours = 0, holidayOtHours = 0;
            int actualDays = 0, restDays = 0, absentDays = 0, missingIn = 0, missingOut = 0;
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
                var isShiftRest  = !isCompDay && assign is not null && assign.ShiftSchedule.IsRestDay(date.DayOfWeek);  // 排的班自己配置的每周休息日
                var isWeekendOff = !isCompDay && assign is null && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;  // 没排班时按全局周末兜底

                // 当天是不是上的夜班：排班里配的是跨天班次/名字带"夜"就算；没排班时按打卡时间兜底
                // （18 点后上班，或下班跨到了第二天），口径和 ComputeNightShiftDaysRangeAsync 保持一致。
                var isNightShift = assign is not null && (assign.ShiftSchedule.IsCrossDay || assign.ShiftSchedule.ShiftName.Contains('夜'));
                if (!isNightShift && rec?.ClockInTime is { } nci)
                {
                    if (nci.Hour >= 18) isNightShift = true;
                    else if (rec.ClockOutTime is { } nco && nco.Date > nci.Date) isNightShift = true;
                }
                row.DailyIsNightShift.Add(isNightShift);

                int? dayHours = null;
                if (rec is not null)
                {
                    if (rec.ActualWorkHours > 0)
                    {
                        dayHours = (int)Math.Floor(rec.ActualWorkHours);   // 每日格子按要求取整（舍去小数）
                        totalWork += FloorToHalf(rec.ActualWorkHours);     // 合计按"半小时"为最小单位累加，保证总数只会是整数或 x.5
                        actualDays++;
                    }
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
                        else if (isShiftRest || isWeekendOff) restOtHours += ot;
                        else weekdayOtHours += ot;
                    }
                }
                else if (!isHolidayOff && !isShiftRest && !isWeekendOff && date <= today)
                {
                    // 完全没有考勤记录，又不是休息/节假日 → 算旷工（和后台旷工判定的口径一致）。
                    // 必须加 date <= today 这个限制：这份报表的统计周期是"上月26号至本月25号"，
                    // 只要在 25 号之前导出，周期里就会包含"还没到的未来几天"——这些天当然不会有考勤记录，
                    // 但它们不是旷工，是"还没发生"，不加这个判断会把每个人都算出一堆莫名其妙的旷工天数。
                    absentDays++;
                }

                if (isHolidayOff || isShiftRest || isWeekendOff) restDays++;

                row.DailyHours.Add(dayHours);
            }

            // 各项工时在上面累加时就已经按"半小时"取整过了，这里直接赋值（合计只会是整数或 x.5）
            row.ActualWorkdays          = actualDays;
            row.RestDays                = restDays;
            row.TotalWorkHours          = totalWork;
            row.LateMinutes             = lateMin;
            row.EarlyLeaveMinutes       = earlyMin;
            row.LateCount               = lateCnt;
            row.EarlyLeaveCount         = earlyCnt;
            row.MissingClockInCount     = missingIn;
            row.MissingClockOutCount    = missingOut;
            row.AbsentDays              = absentDays;
            row.BusinessTripHours       = businessTripHours;
            row.TotalOvertimeHours      = totalOtHours;
            row.WeekdayOvertimeHours    = weekdayOtHours;
            row.RestDayOvertimeHours    = restOtHours;
            row.HolidayOvertimeHours    = holidayOtHours;

            rows.Add(row);
        }

        return new TemplateReportResultDto { StartDate = start, EndDate = end, Dates = dates, Rows = rows };
    }

    /// <summary>
    /// 生成/重算某月的考勤汇总（已存在就更新，没有就新建）。
    /// 处理范围不能只看"现在是否在职"：如果一个人这个月工作过、后来才离职（停用），
    /// 那他这个月的汇总必须照常算出来/保持更新，不能因为人已经离职就让这个月的历史记录消失
    /// ——不然离职员工最后一个月的考勤/工时在报表里会直接对不上账，这是财务和 HR 都要用的数据。
    /// 所以处理范围 = 现在在职的人 ∪ 这个月有考勤记录的人 ∪ 这个月已经生成过汇总的人。
    /// </summary>
    public async Task GenerateMonthlySummaryAsync(int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        var end   = start.AddMonths(1).AddDays(-1);

        var candidateIds = await db.Users.Where(u => u.IsActive).Select(u => u.Id)
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

        foreach (var user in users)
        {
            // 取这个人这个月的每日记录
            var records = await db.AttendanceRecords
                .Where(r => r.UserId == user.Id && r.WorkDate >= start && r.WorkDate <= end)
                .ToListAsync();

            // 应出勤天数：从“月初”和“该员工入职日”里取较晚的一天开始算，
            // 避免月中入职的人被算成全月应出勤、导致出勤率虚低。
            var effStart = user.HireDate is { } hd && hd > start ? hd : start;
            var expected = effStart > end ? 0 : await CountExpectedWorkdaysAsync(effStart, end, user.AttendanceGroupId);

            // 取出已有的汇总，没有就新建
            var summary = await db.MonthlyAttendanceSummaries
                .FirstOrDefaultAsync(s => s.UserId == user.Id && s.Year == year && s.Month == month);

            if (summary is null)
            {
                summary = new MonthlyAttendanceSummary { UserId = user.Id, Year = year, Month = month };
                db.MonthlyAttendanceSummaries.Add(summary);
            }

            // 工时/加班：正常情况下每条记录的 ActualWorkHours 在写入时（本地打卡/钉钉同步/补卡审批）就已经算好了，
            // 这里直接求和即可。仅对「历史遗留、写入时还没补算过」的记录（ActualWorkHours 仍是 0 但有上下班时间）
            // 现场补算，并且顺手写回记录本身——这样老数据只要被打开一次月度报表就能自愈，
            // 不会出现「日明细显示 0、月合计却不是 0」这种对不上的情况。
            var group  = user.AttendanceGroupId.HasValue ? await db.AttendanceGroups.FindAsync(user.AttendanceGroupId.Value) : null;
            var lunch  = group?.LunchBreakMinutes  ?? 60;
            var dinner = group?.DinnerBreakMinutes ?? 30;
            var shiftByDate = (await db.ShiftAssignments
                    .Include(a => a.ShiftSchedule)
                    .Where(a => a.UserId == user.Id && a.WorkDate >= start && a.WorkDate <= end)
                    .ToListAsync())
                .ToDictionary(a => a.WorkDate, a => a.ShiftSchedule);
            decimal totalWork = 0, totalOt = 0;
            foreach (var r in records)
            {
                if (r.ActualWorkHours <= 0 && r.ClockInTime is { } ci && r.ClockOutTime is { } co && co > ci)
                {
                    // 老数据补算工时口径要和写入时一致：早到晚走都不多算钱，加班只认审批（这里不猜、不动 OvertimeHours）
                    shiftByDate.TryGetValue(r.WorkDate, out var shift);
                    var effCi = ClampEffectiveClockIn(r.WorkDate, ci, shift, r.MidCheckTime);
                    var effCo = ClampEffectiveClockOut(r.WorkDate, co, shift);
                    r.ActualWorkHours = ComputeWorkHours(effCi, effCo, lunch, dinner);
                }
                // 每天的工时/加班按"半小时"为最小单位取整后再累加（不足半小时舍去），
                // 保证月度报表上的合计只会是整数或 x.5，不会出现 113.38 这种零碎小数
                totalWork += FloorToHalf(r.ActualWorkHours);
                totalOt   += FloorToHalf(r.OvertimeHours);
            }

            // 根据每日记录算出各项统计
            summary.ExpectedWorkdays  = expected;
            // 实际出勤 = 打了上班卡的天数 + 已批准出差的天数（请假/旷工/未打卡都不算出勤）
            summary.ActualWorkdays    = records.Count(IsPresent);
            // 迟到/早退按「状态」统计（钉钉同步只写状态、不写分钟数，按分钟数会漏算）
            summary.LateCount         = records.Count(r => r.AttendanceStatus == AttendanceStatus.Late);
            summary.EarlyLeaveCount   = records.Count(r => r.AttendanceStatus == AttendanceStatus.EarlyLeave);
            summary.AbsentDays        = records.Count(r => r.AttendanceStatus == AttendanceStatus.Absent);
            // 缺卡：状态=未打卡，或“只打了上/下班其中一次”（这样钉钉数据的缺卡也能识别；出差本就不用打卡，排除）
            summary.NotPunchedCount   = records.Count(r => r.AttendanceStatus == AttendanceStatus.NotPunched
                || ((r.ClockInTime.HasValue ^ r.ClockOutTime.HasValue)
                    && r.AttendanceStatus is not AttendanceStatus.Absent and not AttendanceStatus.OnLeave
                                          and not AttendanceStatus.Holiday and not AttendanceStatus.BusinessTrip));
            summary.LeaveDays         = records.Count(r => r.AttendanceStatus == AttendanceStatus.OnLeave);
            summary.TotalOvertimeHours = totalOt;
            summary.TotalWorkHours    = totalWork;
            summary.ApprovedCount     = approvedByUser.GetValueOrDefault(user.Id);   // 本月审批通过次数（原来漏算，恒为 0）
            summary.UpdatedAt         = DateTime.Now;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>今日考勤看板统计（出勤/旷工/迟到/请假/未打卡人数）。</summary>
    public async Task<AttendanceStatsDto> GetTodayStatsAsync(int? groupId = null)
    {
        var today   = DateOnly.FromDateTime(DateTime.Today);
        var userIds = await BuildUserIdQueryAsync(null, groupId);
        var records = await db.AttendanceRecords
            .Where(r => r.WorkDate == today && userIds.Contains(r.UserId))
            .ToListAsync();

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
            NotPunchedCount = userIds.Count - records.Count(IsPresent),   // 总人数 - 出勤 = 没打卡
            LocationAbnormalCount = records.Count(r => r.LocationAbnormal)
        };
    }

    /// <summary>判断某天算不算“出勤”：打了上班卡，或者当天已批准出差（出差无需打卡也算全勤）。</summary>
    private static bool IsPresent(AttendanceRecord r) => r.ClockInTime.HasValue || r.AttendanceStatus == AttendanceStatus.BusinessTrip;

    /// <summary>
    /// 看板下钻：某统计类别对应的具体人员名单。分类口径和 <see cref="GetTodayStatsAsync"/> 完全一致
    /// （同一批 userIds、同一批 records、同样的判断条件），保证卡片上的数字和点开后名单的人数永远对得上。
    /// </summary>
    public async Task<List<AttendanceRecordDto>> GetTodayStatsDetailAsync(string category, int? groupId = null)
    {
        var today   = DateOnly.FromDateTime(DateTime.Today);
        var userIds = await BuildUserIdQueryAsync(null, groupId);

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
            "notpunched" => userIds.Where(id => !presentIds.Contains(id)).ToList(),   // 含“完全没记录”和“有记录但没打上班卡”两种人
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
    /// 审批通过后回写考勤：补卡 → 补填上/下班时间；加班 → 按审批单时长累加加班工时（加班不再从
    /// 打卡时间估算，必须走这里）；请假/出差 → 把区间内每天置为对应状态。只处理状态为「已通过」的申请。
    /// </summary>
    public async Task UpdateAttendanceAfterApprovalAsync(int approvalRequestId)
    {
        var approval = await db.ApprovalRequests.FindAsync(approvalRequestId);
        if (approval is null || approval.ApprovalStatus != ApprovalStatus.Approved) return;

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
            record.ApprovalNote = $"补卡已审批通过（{approval.RequestNo}）";
            record.UpdatedAt    = DateTime.Now;

            // 补齐上下班两次卡后：重算当天实际工时（工资按工时结算，补完卡必须把工时补准），
            // 并解除“旷工/未打卡”状态（否则人有全天工时却仍被记旷工，工资和出勤对不上）。
            // 加班不再从打卡时间估算，只认「加班申请」审批通过后累加的时长，这里不动 OvertimeHours。
            if (record.ClockInTime is { } ci && record.ClockOutTime is { } co && co > ci)
            {
                var applicant   = await db.Users.FindAsync(approval.ApplicantUserId);
                var shiftAssign = await GetShiftAssignmentAsync(approval.ApplicantUserId, record.WorkDate);

                // 工时口径和本地打卡一致：早到晚走都不多算钱，班次配了午间必打卡窗口、当天又没有打卡落在窗口内，只算下午
                var effectiveClockIn  = await ResolveEffectiveClockInAsync(record, record.WorkDate, ci, shiftAssign?.ShiftSchedule);
                var effectiveClockOut = ClampEffectiveClockOut(record.WorkDate, co, shiftAssign?.ShiftSchedule);
                record.ActualWorkHours = CalcWorkHours(effectiveClockIn, effectiveClockOut, applicant?.AttendanceGroupId);

                if (record.AttendanceStatus is AttendanceStatus.Absent or AttendanceStatus.NotPunched)
                    record.AttendanceStatus = record.LateMinutes > 0       ? AttendanceStatus.Late
                                            : record.EarlyLeaveMinutes > 0 ? AttendanceStatus.EarlyLeave
                                            :                                AttendanceStatus.Normal;
            }
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
            record.ApprovalNote   = $"加班已审批通过（{approval.RequestNo}），{approval.OvertimeDurationHours:0.##} 小时";
            record.UpdatedAt      = DateTime.Now;
        }
        // ── 请假 ──
        else if (approval.ApprovalType == ApprovalType.Leave && approval.LeaveStartTime.HasValue)
        {
            var sd = DateOnly.FromDateTime(approval.LeaveStartTime.Value);
            var ed = DateOnly.FromDateTime(approval.LeaveEndTime ?? approval.LeaveStartTime.Value);

            // 先把请假区间内已经存在的考勤记录一次性整批查出来，按"日期"放进一个字典。
            // 原来的写法是下面 for 循环里每天单独查一次数据库——请十天假就要查十次数据库，
            // 现在改成先查一次、缓存到内存里，下面循环直接从内存里找，结果完全一样，只是数据库跑得更少更快。
            var recordsInRange = await db.AttendanceRecords
                .Where(r => r.UserId == approval.ApplicantUserId && r.WorkDate >= sd && r.WorkDate <= ed)
                .ToDictionaryAsync(r => r.WorkDate);

            for (var d = sd; d <= ed; d = d.AddDays(1))   // 请假区间内每一天
            {
                if (recordsInRange.TryGetValue(d, out var record))
                {
                    record.AttendanceStatus = AttendanceStatus.OnLeave;
                    record.ApprovalNote     = $"请假审批通过（{approval.RequestNo}）";
                    record.UpdatedAt        = DateTime.Now;
                }
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
                record.AttendanceStatus = AttendanceStatus.BusinessTrip;
                record.ActualWorkHours  = shiftAssign?.ShiftSchedule.StandardWorkHours ?? defaultHours;
                record.OvertimeHours    = 0;   // 出差是否加班无法从审批单推断，不猜，需另外提交加班申请
                record.ApprovalNote     = $"出差审批通过（{approval.RequestNo}）"
                    + (string.IsNullOrWhiteSpace(approval.BusinessTripDestination) ? "" : $"，目的地：{approval.BusinessTripDestination}");
                record.UpdatedAt        = DateTime.Now;
            }
        }

        await db.SaveChangesAsync();
    }

    // ── 私有计算方法（下面这些只在本服务内部使用）─────────────────────────────────

    /// <summary>
    /// 算上班状态：实际打卡比「应上班时间 + 迟到容忍」还晚就算迟到。没排班则一律正常。
    /// out lateMinutes 把迟到分钟数“带出去”给调用者。
    /// </summary>
    private static AttendanceStatus CalcClockInStatus(
        DateTime clockIn, ShiftSchedule? shift, out int lateMinutes)
    {
        lateMinutes = 0;
        if (shift is null) return AttendanceStatus.Normal;
        var scheduled = DateOnly.FromDateTime(clockIn).ToDateTime(shift.WorkStartTime);   // 应上班时刻
        var diff      = (int)(clockIn - scheduled).TotalMinutes;                          // 晚了几分钟
        if (diff > shift.LateToleranceMinutes) { lateMinutes = diff; return AttendanceStatus.Late; }
        return AttendanceStatus.Normal;
    }

    /// <summary>
    /// 算下班状态：实际打卡比「应下班时间 − 早退容忍」还早就算早退。夜班的下班时间顺延一天。
    /// out earlyMinutes 把早退分钟数带出去。
    /// ★ "应下班时刻"以 <paramref name="workDate"/>（这条考勤记录归属的那一天，即班次开始的那天）为基准推算，
    /// 不能用 clockOut 打卡那一刻的日期——跨天班次下班时打卡已经是第二天了，
    /// 拿打卡当天的日期再顺延一天会多算出一整天，导致应下班时刻算错。
    /// </summary>
    private static AttendanceStatus CalcClockOutStatus(
        DateOnly workDate, DateTime clockOut, ShiftSchedule? shift, out int earlyMinutes)
    {
        earlyMinutes = 0;
        if (shift is null) return AttendanceStatus.Normal;
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
    /// 算"有效上班时间"（供工时计算用）：查当天有没有任意一次打卡（不分类型）落在班次配置的
    /// 午间必打卡窗口内（没配窗口的跳过这步），顺带把命中的打卡时间写回 record.MidCheckTime，
    /// 再交给 <see cref="ClampEffectiveClockIn"/> 统一算出最终的有效上班时间。
    /// </summary>
    private async Task<DateTime> ResolveEffectiveClockInAsync(
        AttendanceRecord record, DateOnly workDate, DateTime clockIn, ShiftSchedule? shift)
    {
        if (shift?.MidCheckStartTime is null || shift.MidCheckEndTime is null)
        {
            record.MidCheckTime = null;
        }
        else
        {
            var windowStart = ResolveShiftTime(workDate, shift.MidCheckStartTime.Value, shift);
            var windowEnd   = ResolveShiftTime(workDate, shift.MidCheckEndTime.Value, shift);
            var hit = await db.AttendancePunches
                .Where(p => p.UserId == record.UserId && p.PunchTime >= windowStart && p.PunchTime <= windowEnd)
                .OrderBy(p => p.PunchTime)
                .FirstOrDefaultAsync();
            record.MidCheckTime = hit?.PunchTime;
        }

        return ClampEffectiveClockIn(workDate, clockIn, shift, record.MidCheckTime);
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
    /// 算"有效上班时间"（供工时计算用），两条规则叠加，谁把时间往后推得更多就用谁：
    /// 1) 不能靠提前打卡多算钱：有效上班时间不早于排班的应上班时间；
    /// 2) 班次配了午间必打卡窗口、当天又没有任何打卡落在窗口内的，视为"上午没上班"，
    ///    从窗口结束时间起算（只算下午，工资相关，不影响迟到/早退判定）。
    /// ★ 全系统唯一口径：本地打卡、钉钉同步、补卡回写都调这一个，保证结果一致。
    /// </summary>
    public static DateTime ClampEffectiveClockIn(DateOnly workDate, DateTime clockIn, ShiftSchedule? shift, DateTime? midCheckTime)
    {
        var effective = clockIn;

        if (shift is not null)
        {
            var scheduledStart = workDate.ToDateTime(shift.WorkStartTime);
            if (scheduledStart > effective) effective = scheduledStart;
        }

        if (midCheckTime is null && shift?.MidCheckEndTime is not null)
        {
            var windowEnd = ResolveShiftTime(workDate, shift.MidCheckEndTime.Value, shift);
            if (windowEnd > effective) effective = windowEnd;
        }

        return effective;
    }

    /// <summary>
    /// 算"有效下班时间"（供工时计算用）：不能靠晚走多算钱，有效下班时间不晚于排班的应下班时间
    /// （跨天班次顺延到第二天）；提前下班（早退）不受影响，仍按实际下班时间算，正常反映早退少算的工时。
    /// 加班不再从打卡时间估算，只认「加班申请」审批通过后累加到 OvertimeHours 的时长。
    /// ★ 全系统唯一口径：本地打卡、钉钉同步、补卡回写都调这一个，保证结果一致。
    /// </summary>
    public static DateTime ClampEffectiveClockOut(DateOnly workDate, DateTime clockOut, ShiftSchedule? shift)
    {
        if (shift is null) return clockOut;
        var scheduledEnd = workDate.ToDateTime(shift.WorkEndTime);
        if (shift.IsCrossDay) scheduledEnd = scheduledEnd.AddDays(1);
        return scheduledEnd < clockOut ? scheduledEnd : clockOut;
    }

    /// <summary>
    /// 把工时数规范成"半小时"为最小单位：不足半小时的零头舍去（1.2→1.0，1.7→1.5，1.5 不变）。
    /// 月度报表里所有对外展示/导出的工时合计都过这一道，保证只会出现整数或 x.5，
    /// 和"每日格子舍去小数"是同一个"不足不计"的口径（工资按工时结算，宁少勿多）。
    /// </summary>
    public static decimal FloorToHalf(decimal hours) => Math.Floor(hours * 2) / 2;

    /// <summary>
    /// 纯计算：由上下班时间 + 午休/晚餐扣时算实际工时（小时）。上班超 6h 扣午休、超 9h 再扣晚餐。
    /// ★ 全系统唯一的工时公式：本地打卡、钉钉同步、补卡回写、月度汇总都调这一个，保证口径一致（工资按工时结算）。
    /// </summary>
    public static decimal ComputeWorkHours(DateTime clockIn, DateTime clockOut, int lunchBreak, int dinnerBreak)
    {
        var minutes = (decimal)(clockOut - clockIn).TotalMinutes;   // 在岗总分钟（夜班下班在第二天也没问题）
        if (minutes <= 0) return 0;
        if (minutes > 6 * 60) minutes -= lunchBreak;
        if (minutes > 9 * 60) minutes -= dinnerBreak;
        return Math.Max(0, Math.Round(minutes / 60, 2));
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
    /// ● 调班补班日：哪怕是周末也算出勤；
    /// ● 普通工作日：只要不是法定节假日/公司休息日，就算出勤。
    /// </summary>
    private async Task<int> CountExpectedWorkdaysAsync(DateOnly start, DateOnly end, int? groupId)
    {
        var holidays = await db.Holidays
            .Where(h => h.HolidayDate >= start && h.HolidayDate <= end
                     && (h.AttendanceGroupId == null || h.AttendanceGroupId == groupId))
            .ToListAsync();

        var count = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var isWeekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var holiday   = holidays.FirstOrDefault(h => h.HolidayDate == d);
            if (holiday?.HolidayType == HolidayType.CompensatoryWorkDay) count++;        // 补班日算
            else if (!isWeekend && holiday?.HolidayType is not HolidayType.LegalHoliday  // 工作日且非节假日算
                                                       and not HolidayType.CompanyRestDay) count++;
        }
        return count;
    }

    /// <summary>按部门/考勤组圈出在职员工的编号列表。</summary>
    private async Task<List<int>> BuildUserIdQueryAsync(int? deptId, int? groupId)
    {
        var q = db.Users.Where(u => u.IsActive).AsQueryable();
        if (deptId.HasValue)  q = q.Where(u => u.DepartmentId == deptId.Value);
        if (groupId.HasValue) q = q.Where(u => u.AttendanceGroupId == groupId.Value);
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
        MidCheckTime     = r.MidCheckTime,
        AttendanceStatus = r.AttendanceStatus,
        StatusText       = StatusText(r.AttendanceStatus),
        StatusCssClass   = StatusCss(r.AttendanceStatus),
        LateMinutes      = r.LateMinutes,
        EarlyLeaveMinutes = r.EarlyLeaveMinutes,
        ActualWorkHours  = r.ActualWorkHours,
        OvertimeHours    = r.OvertimeHours,
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
