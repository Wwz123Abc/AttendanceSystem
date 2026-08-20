using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 熵基考勤机打卡数据落库：原始打卡流水去重写入 + 按人/日 upsert 考勤日记录，
/// 设备已经明确告诉我们是上班还是下班（Status 字段），迟到/早退状态当场按班次计算。
/// </summary>
public class ZKDeviceSyncService(AttendanceDbContext db, ILogger<ZKDeviceSyncService> logger) : IZKDeviceSyncService
{
    public async Task ProcessAttLogAsync(string sn, List<ZKAttLogRow> rows, CancellationToken ct = default)
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
        var dates = rows.Select(r => DateOnly.FromDateTime(r.Time)).Distinct().ToList();
        if (uids.Count == 0) return;

        // 2) 预加载：考勤组午休/晚餐扣时、已有打卡流水（去重用）、已有考勤日记录、当天排班
        var groupBreaks = await db.AttendanceGroups
            .Select(g => new { g.Id, g.LunchBreakMinutes, g.DinnerBreakMinutes })
            .ToDictionaryAsync(g => g.Id, g => (g.LunchBreakMinutes, g.DinnerBreakMinutes), ct);

        var punchSet = (await db.AttendancePunches
                .Where(p => uids.Contains(p.UserId) && dates.Contains(DateOnly.FromDateTime(p.PunchTime)))
                .Select(p => new { p.UserId, p.PunchType, p.PunchTime })
                .ToListAsync(ct))
            .Select(p => (p.UserId, p.PunchType, p.PunchTime))
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
        foreach (var r in rows.OrderBy(r => r.Time))   // 按时间顺序处理，保证"当天最早上班/最晚下班"取值正确
        {
            if (!userByPin.TryGetValue(r.Pin, out var uid)) continue;

            var type = r.Status switch
            {
                0 => PunchType.ClockIn,
                1 => PunchType.ClockOut,
                2 or 3 => PunchType.MidCheck,   // 外出/外出返回，暂按"午间打卡"记录，不参与迟到早退判定
                _ => PunchType.ClockIn
            };
            var workDate = DateOnly.FromDateTime(r.Time);

            if (!punchSet.Add((uid, type, r.Time))) continue;   // 去重：同一人同类型同一分钟已经存过就跳过

            db.AttendancePunches.Add(new AttendancePunch
            {
                UserId     = uid,
                PunchTime  = r.Time,
                PunchType  = type,
                DeviceInfo = $"ZKDevice:{sn}",
                IsValid    = true,
                CreatedAt  = DateTime.Now
            });

            if (!recordMap.TryGetValue((uid, workDate), out var record))
            {
                record = new AttendanceRecord { UserId = uid, WorkDate = workDate };
                db.AttendanceRecords.Add(record);
                recordMap[(uid, workDate)] = record;
            }

            shiftByUserDate.TryGetValue((uid, workDate), out var shift);

            if (type == PunchType.ClockIn && (record.ClockInTime is null || r.Time < record.ClockInTime))
            {
                record.ClockInTime = r.Time;
                var status = AttendanceService.CalcClockInStatus(r.Time, shift, out var lateMin);
                record.LateMinutes = lateMin;
                if (status == AttendanceStatus.Late) record.AttendanceStatus = AttendanceStatus.Late;
            }
            else if (type == PunchType.ClockOut && (record.ClockOutTime is null || r.Time > record.ClockOutTime))
            {
                record.ClockOutTime = r.Time;
                var status = AttendanceService.CalcClockOutStatus(workDate, r.Time, shift, out var earlyMin);
                record.EarlyLeaveMinutes = earlyMin;
                if (status == AttendanceStatus.EarlyLeave && record.AttendanceStatus != AttendanceStatus.Late)
                    record.AttendanceStatus = AttendanceStatus.EarlyLeave;
            }

            record.Remark    = "熵基考勤机同步";
            record.UpdatedAt = DateTime.Now;

            // 工时：上下班都齐了才算，公式和全系统唯一口径完全一致（早到晚走不多算钱）
            if (record.ClockInTime is { } ci && record.ClockOutTime is { } co && co > ci)
            {
                var (lunch, dinner) = groupIdByUser.TryGetValue(uid, out var gid) && gid.HasValue
                                      && groupBreaks.TryGetValue(gid.Value, out var brk)
                    ? brk : (60, 30);
                var effectiveClockIn  = AttendanceService.ClampEffectiveClockIn(workDate, ci, shift, []);
                var effectiveClockOut = AttendanceService.ClampEffectiveClockOut(workDate, co, shift);
                record.ActualWorkHours = AttendanceService.ComputeWorkHours(effectiveClockIn, effectiveClockOut, lunch, dinner);
            }
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
}
