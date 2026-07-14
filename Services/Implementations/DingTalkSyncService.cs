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
/// 钉钉打卡数据同步服务。流程：
/// 1) 取本地已映射钉钉 userid 的用户；2) 调客户端拉取打卡结果；
/// 3) 写入原始打卡流水 AttendancePunch（去重）；4) 按「人+日」汇总 upsert 考勤日记录 AttendanceRecord。
/// 说明：日记录里的状态直接采用钉钉判定的 timeResult；实际工时/加班时长会在同步时用和本地打卡完全相同的
/// 公式当场算好写入（工资按工时结算，不能留 0 等报表兜底），见下方 4) 步骤。
/// </summary>
public class DingTalkSyncService(
    AttendanceDbContext db,
    IDingTalkAttendanceClient client,
    IDingTalkContactClient contactClient,
    IDingTalkLeaveClient leaveClient,
    IConfiguration configuration,
    IOptions<DingTalkOptions> options,
    ILogger<DingTalkSyncService> logger) : IDingTalkSyncService
{
    private readonly DingTalkOptions _opt = options.Value;

    public async Task<DingTalkSyncResultDto> SyncAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        // 1) 本地「钉钉 userid -> 本地 User.Id」映射（顺带取考勤组，算工时要用组里的午休/晚餐扣时）
        var mappedUsers = await db.Users
            .Where(u => u.DingTalkUserId != null && u.DingTalkUserId != "")
            .Select(u => new { u.Id, u.DingTalkUserId, u.AttendanceGroupId })
            .ToListAsync(ct);
        var userMap       = mappedUsers.ToDictionary(u => u.DingTalkUserId!, u => u.Id);
        var groupIdByUser = mappedUsers.ToDictionary(u => u.Id, u => u.AttendanceGroupId);

        // 各考勤组的午休/晚餐扣时（工资按工时结算，同步时必须把工时算准）
        var groupBreaks = await db.AttendanceGroups
            .Select(g => new { g.Id, g.LunchBreakMinutes, g.DinnerBreakMinutes })
            .ToDictionaryAsync(g => g.Id, g => (g.LunchBreakMinutes, g.DinnerBreakMinutes), ct);

        // 各考勤组是否开了定位打卡 + 配置的打卡地点（用来比对钉钉上报的定位是否在范围内）
        var groupEnableLocation = await db.AttendanceGroups
            .Select(g => new { g.Id, g.EnableLocationPunch })
            .ToDictionaryAsync(g => g.Id, g => g.EnableLocationPunch, ct);
        var groupLocations = (await db.AttendanceGroupLocations.ToListAsync(ct))
            .GroupBy(l => l.AttendanceGroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new DingTalkSyncResultDto { From = from, To = to, MappedUsers = userMap.Count };

        if (userMap.Count == 0)
        {
            result.Success = false;
            result.Message = "没有任何用户配置了钉钉 userid 映射（User.DingTalkUserId），无法同步";
            logger.LogWarning(result.Message);
            return result;
        }

        // 2) 拉取钉钉打卡结果
        var records = await client.ListAttendanceAsync(userMap.Keys, from, to, ct);
        result.Pulled = records.Count;

        // 预加载本时间窗内已存在的打卡流水与考勤日记录，循环内只在内存里判重/命中，避免逐条查库（N+1）
        var uids     = userMap.Values.ToHashSet();
        var fromDate = DateOnly.FromDateTime(from);
        var toDate   = DateOnly.FromDateTime(to);

        // punchSet 同时承担「DB 已存在」与「本批次重复」两种去重：Add 返回 false 即视为已存在
        var punchSet = (await db.AttendancePunches
                .Where(p => uids.Contains(p.UserId) && p.PunchTime >= from && p.PunchTime <= to)
                .Select(p => new { p.UserId, p.PunchType, p.PunchTime })
                .ToListAsync(ct))
            .Select(p => (p.UserId, p.PunchType, p.PunchTime))
            .ToHashSet();

        var recordMap = (await db.AttendanceRecords
                .Where(r => uids.Contains(r.UserId) && r.WorkDate >= fromDate && r.WorkDate <= toDate)
                .ToListAsync(ct))
            .ToDictionary(r => (r.UserId, r.WorkDate));

        // 本时间窗内的排班（有排班的按班次精确算加班；没排班的按“超 8 小时”估算）
        var shiftByUserDate = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => uids.Contains(a.UserId) && a.WorkDate >= fromDate && a.WorkDate <= toDate)
                .ToListAsync(ct))
            .ToDictionary(a => (a.UserId, a.WorkDate), a => a.ShiftSchedule);

        var dayAgg = new Dictionary<(int UserId, DateOnly Date), DayPunch>();

        foreach (var r in records)
        {
            if (!userMap.TryGetValue(r.UserId, out var localUid)) continue;  // 未映射跳过

            // 该行归属的「工作日」：优先 workDate；缺失时退回打卡时刻所在日期
            long dateMs = r.WorkDate > 0 ? r.WorkDate : r.UserCheckTime;
            if (dateMs <= 0) continue;                                       // 既无工作日也无打卡时刻，无法归日
            var workDate = ToLocalDate(dateMs);

            if (!dayAgg.TryGetValue((localUid, workDate), out var agg))
                dayAgg[(localUid, workDate)] = agg = new DayPunch();

            if (r.UserCheckTime > 0)
            {
                var time = ToLocalMinute(r.UserCheckTime);                   // 打卡时刻（精确到分钟）
                var type = string.Equals(r.CheckType, "OnDuty", StringComparison.OrdinalIgnoreCase)
                    ? PunchType.ClockIn : PunchType.ClockOut;

                double? lat = double.TryParse(r.UserLatitude,  out var la) && la != 0 ? la : null;
                double? lng = double.TryParse(r.UserLongitude, out var lo) && lo != 0 ? lo : null;

                // 定位比对：员工的考勤组开了定位打卡、且配了地点时，比对钉钉上报的坐标离最近的地点多远
                bool?  locationValid = null;
                string? locationNote = null;
                if (lat.HasValue && lng.HasValue
                    && groupIdByUser.TryGetValue(localUid, out var gidForLoc) && gidForLoc.HasValue
                    && groupEnableLocation.GetValueOrDefault(gidForLoc.Value)
                    && groupLocations.TryGetValue(gidForLoc.Value, out var locs) && locs.Count > 0)
                {
                    var nearest = locs
                        .Select(l => new
                        {
                            l.LocationName,
                            l.RadiusMeters,
                            Distance = AttendanceService.HaversineMeters(lat.Value, lng.Value, l.Latitude, l.Longitude)
                        })
                        .OrderBy(x => x.Distance).First();
                    locationValid = nearest.Distance <= nearest.RadiusMeters;
                    if (!locationValid.Value)
                        locationNote = $"打卡地点距最近的「{nearest.LocationName ?? "打卡点"}」{nearest.Distance:F0} 米，超出有效范围 {nearest.RadiusMeters} 米";
                }

                // 3) 去重写入原始打卡流水
                if (punchSet.Add((localUid, type, time)))
                {
                    db.AttendancePunches.Add(new AttendancePunch
                    {
                        UserId        = localUid,
                        PunchTime     = time,
                        PunchType     = type,
                        Latitude      = lat,
                        Longitude     = lng,
                        Address       = r.UserAddress,
                        DeviceInfo    = $"DingTalk:{r.SourceType}",
                        IsValid       = true,
                        LocationValid = locationValid,
                        CreatedAt     = DateTime.Now
                    });
                    result.PunchAdded++;
                }

                agg.Apply(type, time, r.TimeResult);
                if (locationValid == false) agg.MarkLocationAbnormal(locationNote!);   // 需要人工审核，但不影响正常的迟到/旷工判定
            }
            else
            {
                // 没有实际打卡时刻的行（旷工 Absenteeism / 未打卡 NotSigned）：
                // 写不了打卡流水，但要保留这个「结果」用于判定旷工，
                // 否则缺勤永远同步不进来（这正是看板缺勤恒为 0 的根因）。
                agg.ApplyResultOnly(r.TimeResult);
            }
        }

        // 4) upsert 考勤日记录（命中预加载的 recordMap 则更新，未命中才新建）
        foreach (var ((uid, date), agg) in dayAgg)
        {
            // 整天既没打卡、也不是旷工（如仅有「未打卡」标记）→ 不建无意义的空记录
            if (!agg.HasData) continue;

            if (!recordMap.TryGetValue((uid, date), out var record))
            {
                record = new AttendanceRecord { UserId = uid, WorkDate = date };
                db.AttendanceRecords.Add(record);
                recordMap[(uid, date)] = record;
            }

            if (agg.ClockIn  is not null) record.ClockInTime  = agg.ClockIn;
            if (agg.ClockOut is not null) record.ClockOutTime = agg.ClockOut;
            record.AttendanceStatus     = agg.ResolveStatus();
            record.Remark               = "钉钉同步";
            record.LocationAbnormal     = agg.LocationAbnormal;      // 定位异常只是提醒，不影响上面的迟到/旷工判定
            record.LocationAbnormalNote = agg.LocationAbnormalNote;
            record.UpdatedAt            = DateTime.Now;

            shiftByUserDate.TryGetValue((uid, date), out var shift);

            // 午间必打卡窗口：班次配了窗口的，当天有没有任意一次打卡（不分上/下班类型）落在窗口内。
            // 钉钉的 OnDuty/OffDuty 分类不代表"这是午间那次"，所以不看类型，只看时间是否落在窗口内。
            record.MidCheckTime = shift?.MidCheckStartTime is not null && shift.MidCheckEndTime is not null
                ? agg.AllTimes
                    .Where(t => t >= AttendanceService.ResolveShiftTime(date, shift.MidCheckStartTime.Value, shift)
                             && t <= AttendanceService.ResolveShiftTime(date, shift.MidCheckEndTime.Value, shift))
                    .OrderBy(t => t)
                    .Cast<DateTime?>()
                    .FirstOrDefault()
                : null;

            // ★ 工时（工资按工时结算，必须在同步时算准写进日记录，不能留 0 等汇总兜底）：
            //   上下班卡齐了就按「打卡时长 − 午休/晚餐」算实际工时，公式与本地打卡完全相同；
            //   早到晚走都不多算钱：有效上班时间不早于应上班时间，有效下班时间不晚于应下班时间
            //   （午间必打卡窗口配了但当天没打→视为"上午没上班"只算下午，见 ClampEffectiveClockIn）；
            //   加班不再从打卡时间估算，只认「加班申请」审批通过后累加的时长，这里不动 OvertimeHours。
            if (record.ClockInTime is { } rci && record.ClockOutTime is { } rco && rco > rci)
            {
                var (lunch, dinner) = groupIdByUser.TryGetValue(uid, out var gid) && gid.HasValue
                                      && groupBreaks.TryGetValue(gid.Value, out var brk)
                    ? brk : (60, 30);

                var effectiveClockIn  = AttendanceService.ClampEffectiveClockIn(date, rci, shift, record.MidCheckTime);
                var effectiveClockOut = AttendanceService.ClampEffectiveClockOut(date, rco, shift);
                record.ActualWorkHours = AttendanceService.ComputeWorkHours(effectiveClockIn, effectiveClockOut, lunch, dinner);
            }
            result.RecordUpserted++;
        }

        // 5) 拉取并写入钉钉「请假」：把请假区间内每天的考勤记录置为「请假」（覆盖缺勤/正常）。
        //    打卡结果接口不含请假，必须调请假状态接口；未开通权限时优雅跳过，不影响打卡同步。
        if (_opt.EnableLeaveSync)
        {
            try
            {
                var leaves = await leaveClient.ListLeaveStatusAsync(userMap.Keys, from, to, ct);
                result.LeavePulled = leaves.Count;

                foreach (var lv in leaves)
                {
                    if (!userMap.TryGetValue(lv.UserId, out var localUid)) continue;  // 未映射跳过
                    if (lv.StartTime <= 0 || lv.EndTime <= 0) continue;               // 时间缺失跳过

                    // 请假区间裁剪到本次同步窗口内，逐天置为「请假」
                    var sd = ToLocalDate(lv.StartTime);
                    var ed = ToLocalDate(lv.EndTime);
                    if (sd < fromDate) sd = fromDate;
                    if (ed > toDate)   ed = toDate;

                    var leaveLabel = !string.IsNullOrWhiteSpace(lv.LeaveName) ? lv.LeaveName!
                                   : !string.IsNullOrWhiteSpace(lv.LeaveCode) ? lv.LeaveCode!
                                   : "请假";

                    for (var d = sd; d <= ed; d = d.AddDays(1))
                    {
                        if (!recordMap.TryGetValue((localUid, d), out var record))
                        {
                            record = new AttendanceRecord { UserId = localUid, WorkDate = d };
                            db.AttendanceRecords.Add(record);
                            recordMap[(localUid, d)] = record;
                        }
                        record.AttendanceStatus = AttendanceStatus.OnLeave;   // 请假优先级高于缺勤/正常
                        record.Remark           = $"钉钉请假：{leaveLabel}";
                        record.UpdatedAt        = DateTime.Now;
                        result.LeaveApplied++;
                    }
                }
            }
            catch (Exception ex)
            {
                // 最常见就是没开「考勤/假期」数据权限：记下原因附到结果里，但不让整个同步失败
                result.LeaveSkippedReason = ex.Message;
                logger.LogWarning(ex, "钉钉请假同步已跳过（通常是未开通「考勤/假期数据」权限）");
            }
        }

        await db.SaveChangesAsync(ct);

        var leaveMsg = result.LeaveSkippedReason is not null
            ? $"，请假同步已跳过（{result.LeaveSkippedReason}）"
            : $"，请假记录 {result.LeavePulled} 条 / 置假 {result.LeaveApplied} 天";
        result.Message = $"同步完成：映射用户 {result.MappedUsers} 人，拉取 {result.Pulled} 条，" +
                         $"新增打卡流水 {result.PunchAdded} 条，更新日记录 {result.RecordUpserted} 条" + leaveMsg;
        logger.LogInformation(result.Message);
        return result;
    }

    public async Task<DingTalkAutoMapResultDto> AutoMapByJobNumberAsync(bool overwrite = false, CancellationToken ct = default)
    {
        // 钉钉没有"按工号查 userid"的接口，所以拉一次通讯录（含 userid+工号），在本地按工号匹配
        DingTalkContactSnapshot snapshot;
        try
        {
            snapshot = await contactClient.ListAllAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "钉钉自动映射：拉取通讯录失败");
            return new DingTalkAutoMapResultDto
            {
                Success = false,
                Message = "拉取钉钉通讯录失败，请检查 AppKey/AppSecret、通讯录读权限与 IP 白名单：" + ex.Message
            };
        }

        // 钉钉「工号 -> userid」索引（忽略没填工号的）
        var byJob = new Dictionary<string, string>();
        foreach (var du in snapshot.Users)
            if (!string.IsNullOrWhiteSpace(du.JobNumber) && !string.IsNullOrEmpty(du.UserId))
                byJob[du.JobNumber!.Trim()] = du.UserId;

        // 本地待映射员工：有工号，且（未映射 或 勾选覆盖）
        var users = await db.Users
            .Where(u => u.EmployeeNo != "" &&
                        (overwrite || u.DingTalkUserId == null || u.DingTalkUserId == ""))
            .ToListAsync(ct);

        var result = new DingTalkAutoMapResultDto { Total = users.Count };
        foreach (var u in users)
        {
            if (byJob.TryGetValue(u.EmployeeNo.Trim(), out var uid))
            {
                u.DingTalkUserId = uid;
                u.UpdatedAt      = DateTime.Now;
                result.Mapped++;
            }
            else
            {
                result.NotFound++;   // 该工号在钉钉通讯录里找不到（钉钉没填工号 / 工号不一致）
            }
        }

        if (result.Mapped > 0) await db.SaveChangesAsync(ct);

        result.Message = $"自动映射（按工号）完成：待映射 {result.Total} 人，成功 {result.Mapped}，" +
                         $"工号在钉钉查无 {result.NotFound}（钉钉通讯录读到 {snapshot.Users.Count} 人）";
        if (result.Total > 0 && result.Mapped == 0)
            result.Success = false;

        logger.LogInformation(result.Message);
        return result;
    }

    public async Task<DingTalkImportResultDto> ImportEmployeesAsync(CancellationToken ct = default)
    {
        var snapshot  = await contactClient.ListAllAsync(ct);
        var dingUsers = snapshot.Users;
        var result    = new DingTalkImportResultDto { TotalFromDingTalk = dingUsers.Count };

        if (dingUsers.Count == 0)
        {
            result.Success = false;
            result.Message = "未从钉钉通讯录读到任何员工：请确认应用已发布、且可用范围为「全部员工」";
            logger.LogWarning(result.Message);
            return result;
        }

        var now         = DateTime.Now;
        var companyName = string.IsNullOrWhiteSpace(snapshot.CompanyName) ? "钉钉企业" : snapshot.CompanyName!.Trim();

        // 1) 同步部门：按 DeptCode="DT{钉钉部门id}" 唯一匹配，写入部门名与「所属公司」
        var deptCodeMap = (await db.Departments.Where(d => d.DeptCode != null).ToListAsync(ct))
            .ToDictionary(d => d.DeptCode!, d => d);
        foreach (var dd in snapshot.Departments)
        {
            var code = $"DT{dd.DeptId}";
            if (!deptCodeMap.TryGetValue(code, out var dept))
            {
                dept = new Department { DeptCode = code, CreatedAt = now };
                db.Departments.Add(dept);
                deptCodeMap[code] = dept;
            }
            dept.DeptName    = string.IsNullOrWhiteSpace(dd.Name) ? code : dd.Name!;
            dept.CompanyName = companyName;     // 所属公司
            dept.IsActive    = true;
            dept.UpdatedAt   = now;
        }
        await db.SaveChangesAsync(ct);          // 先存部门以拿到本地 Id

        // 部门层级：钉钉 parent_id → 本地部门 Id
        foreach (var dd in snapshot.Departments)
            if (dd.ParentId > 0 &&
                deptCodeMap.TryGetValue($"DT{dd.DeptId}", out var dept) &&
                deptCodeMap.TryGetValue($"DT{dd.ParentId}", out var parent))
                dept.ParentId = parent.Id;

        // 钉钉部门id → 本地部门Id
        var deptIdToLocal = snapshot.Departments
            .Where(dd => deptCodeMap.ContainsKey($"DT{dd.DeptId}"))
            .ToDictionary(dd => dd.DeptId, dd => deptCodeMap[$"DT{dd.DeptId}"].Id);
        result.DepartmentsSynced = snapshot.Departments.Count;

        // 2) 确保存在「{公司}考勤组」（带所属公司），导入员工统一归入
        var groupName = $"{companyName}考勤组";
        var group = await db.AttendanceGroups.FirstOrDefaultAsync(g => g.GroupName == groupName, ct);
        if (group is null)
        {
            group = new AttendanceGroup { GroupName = groupName, CompanyName = companyName, IsActive = true, CreatedAt = now, UpdatedAt = now };
            db.AttendanceGroups.Add(group);
            await db.SaveChangesAsync(ct);
        }
        var groupId = group.Id;

        // 3) 导入员工：先按 userid 再按「工号」匹配已有员工（工号 = 本地 EmployeeNo = 钉钉 job_number），缺失则新建
        var locals     = await db.Users.ToListAsync(ct);
        var byUserId   = locals.Where(u => !string.IsNullOrEmpty(u.DingTalkUserId))
                               .ToDictionary(u => u.DingTalkUserId!, u => u);
        var byEmpNo    = locals.GroupBy(u => u.EmployeeNo).ToDictionary(g => g.Key, g => g.First());
        var usedEmpNos = locals.Select(u => u.EmployeeNo).ToHashSet();
        var defaultPwdHash = UserService.HashPassword(configuration["AppSettings:DefaultPassword"] ?? "123456");

        foreach (var du in dingUsers)
        {
            // 主部门 = 员工首个钉钉部门映射到本地
            int? deptId = null;
            var firstDept = du.DeptIdList?.FirstOrDefault() ?? 0;
            if (firstDept > 0 && deptIdToLocal.TryGetValue(firstDept, out var ld)) deptId = ld;

            // 入职日期（钉钉 hired_date 毫秒戳）
            DateOnly? hireDate = du.HiredDate is > 0 ? ToLocalDate(du.HiredDate.Value) : null;

            // 先按 userid 命中；再按工号命中（把已存在的真实员工对接上 userid）
            User? local = byUserId.GetValueOrDefault(du.UserId);
            if (local is null && !string.IsNullOrWhiteSpace(du.JobNumber))
                local = byEmpNo.GetValueOrDefault(du.JobNumber!.Trim());

            if (local is not null)
            {
                local.DingTalkUserId = du.UserId;
                if (!string.IsNullOrWhiteSpace(du.Mobile)) local.Phone    = du.Mobile;
                if (!string.IsNullOrWhiteSpace(du.Name))   local.RealName = du.Name!;
                // 部门/考勤组/入职日期只在为空时补，不覆盖管理员已有的手动设置
                local.DepartmentId      ??= deptId;
                local.AttendanceGroupId ??= groupId;
                local.HireDate          ??= hireDate;
                local.UpdatedAt = now;
                byUserId[du.UserId] = local;     // 防止后续重复命中又算一次
                result.Updated++;
            }
            else
            {
                var empNo = GenerateEmployeeNo(du, usedEmpNos);
                db.Users.Add(new User
                {
                    EmployeeNo        = empNo,
                    RealName          = string.IsNullOrWhiteSpace(du.Name) ? empNo : du.Name!,
                    PasswordHash      = defaultPwdHash,
                    Phone             = string.IsNullOrWhiteSpace(du.Mobile) ? null : du.Mobile,
                    Position          = du.Title,
                    Role              = UserRole.Employee,
                    DepartmentId      = deptId,
                    AttendanceGroupId = groupId,
                    HireDate          = hireDate,
                    DingTalkUserId    = du.UserId,
                    IsActive          = true,
                    CreatedAt         = now,
                    UpdatedAt         = now
                });
                usedEmpNos.Add(empNo);
                result.Created++;
            }
        }

        await db.SaveChangesAsync(ct);

        result.Message = $"导入完成：公司「{companyName}」，部门 {result.DepartmentsSynced} 个，" +
                         $"钉钉员工 {result.TotalFromDingTalk} 人，新建 {result.Created}，更新 {result.Updated}；" +
                         $"统一归入「{groupName}」（如需按部门细分考勤组，请到「考勤组管理」手动配置部门跟随）";
        logger.LogInformation(result.Message);
        return result;
    }

    /// <summary>为导入的新员工生成唯一工号：优先用钉钉工号，否则用 userid，冲突则加后缀。</summary>
    private static string GenerateEmployeeNo(DingTalkDeptUser du, HashSet<string> used)
    {
        var baseNo = !string.IsNullOrWhiteSpace(du.JobNumber) ? du.JobNumber!.Trim() : du.UserId;
        var no = baseNo;
        for (int i = 1; used.Contains(no); i++) no = $"{baseNo}_{i}";
        return no;
    }

    /// <summary>Unix 毫秒时间戳 → 本地时间，并截断到分钟（与本系统打卡精度一致，便于去重）。</summary>
    private static DateTime ToLocalMinute(long ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
    }

    /// <summary>Unix 毫秒时间戳 → 本地日期。</summary>
    private static DateOnly ToLocalDate(long ms)
        => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime);

    /// <summary>单人单日打卡汇总：取最早上班 / 最晚下班，并按钉钉 timeResult 推导状态。</summary>
    private sealed class DayPunch
    {
        public DateTime? ClockIn  { get; private set; }
        public DateTime? ClockOut { get; private set; }
        private bool _late, _early, _absent;

        /// <summary>这一天全部打卡时刻（不分类型），用来判断有没有打卡落在班次配置的午间必打卡窗口内。</summary>
        public List<DateTime> AllTimes { get; } = [];

        /// <summary>这一天是否有可写入的数据（有打卡，或被判旷工）。仅「未打卡」这种残缺标记不算。</summary>
        public bool HasData => ClockIn is not null || ClockOut is not null || _absent;

        /// <summary>这一天是否有定位异常，需要人工审核（不影响正常的迟到/旷工判定）。</summary>
        public bool LocationAbnormal { get; private set; }
        public string? LocationAbnormalNote { get; private set; }

        /// <summary>标记这一天有一次定位对不上（记最后一次触发的说明即可，不需要罗列每一次）。</summary>
        public void MarkLocationAbnormal(string note)
        {
            LocationAbnormal     = true;
            LocationAbnormalNote = note;
        }

        public void Apply(PunchType type, DateTime time, string? timeResult)
        {
            AllTimes.Add(time);
            if (type == PunchType.ClockIn)
            {
                if (ClockIn is null || time < ClockIn) ClockIn = time;
                if (timeResult is "Late" or "SeriousLate") _late = true;
            }
            else
            {
                if (ClockOut is null || time > ClockOut) ClockOut = time;
                if (timeResult is "Early") _early = true;
            }
            if (timeResult is "Absenteeism") _absent = true;
        }

        /// <summary>只有结果、没有打卡时刻的行（旷工/未打卡）：仅据此判定是否旷工。</summary>
        public void ApplyResultOnly(string? timeResult)
        {
            if (timeResult is "Absenteeism") _absent = true;
        }

        public AttendanceStatus ResolveStatus()
        {
            if (_absent) return AttendanceStatus.Absent;
            if (_late)   return AttendanceStatus.Late;
            if (_early)  return AttendanceStatus.EarlyLeave;
            return AttendanceStatus.Normal;
        }
    }
}
