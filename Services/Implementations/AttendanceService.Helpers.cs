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

/// <summary>内部辅助：夜班天数、应出勤天数、DTO 映射、距离计算、状态文字与样式（<see cref="AttendanceService"/> 的一部分）。</summary>
public partial class AttendanceService
{
    /// <summary>
    /// 批量算某批员工某月的“夜班天数”（不存表，报表读取时实时算）。
    /// 判定：当天实际出勤(打了上班卡)，且满足以下任一：
    ///   ① 当天排的班是夜班(跨天班次 或 班次名含“夜”)；
    ///   ② 当天没有排班时按打卡时间兜底：18 点后上班，或下班跨到了第二天（适配钉钉数据）；排了白班/中班的不套用。
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

        // 有排班的 (用户, 日期)：兜底判断只对"没排班"的日子生效
        var anyAssign = (await db.ShiftAssignments
                .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= start && a.WorkDate <= end)
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
            if (!isNight && !anyAssign.Contains((r.UserId, r.WorkDate)) && r.ClockInTime is { } ci)
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
    private static int CountExpectedWorkdays(DateOnly start, DateOnly end, Dictionary<DateOnly, ShiftSchedule> shiftByDate)
    {
        var count = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (!IsShiftWeeklyRestDay(d, shiftByDate.TryGetValue(d, out var shift) ? shift : null)) count++;
        }
        return count;
    }

    /// <summary>
    /// 按部门/考勤组圈出在职员工的编号列表。
    /// excludeExempt=true 时再排掉免考勤的人（非"普通员工"角色，或勾了"免考勤"）：看板、报表、考勤记录列表
    /// 都只反映"需要考勤的人"，不然正式工每天都会被算进"未打卡"，名单里也全是他们
    /// （2026-09-24 补漏；2026-10-07 起正式工角色一律免考勤，不再靠逐个勾选）。
    /// </summary>
    private async Task<List<int>> BuildUserIdQueryAsync(int? deptId, int? groupId, HashSet<int>? deptIds = null, bool excludeExempt = false)
        => await UserIdQuery(deptId, groupId, deptIds, excludeExempt).ToListAsync();

    /// <summary>同上，但不立刻查库，返回可以嵌进别的查询里当子查询用的 id 查询（看板这类只要数量的统计用，不必把几千个 id 拉回来再塞进 IN 列表）。</summary>
    private IQueryable<int> UserIdQuery(int? deptId, int? groupId, HashSet<int>? deptIds = null, bool excludeExempt = false)
    {
        var q = db.Users.Where(u => u.IsActive).AsQueryable();
        if (excludeExempt) q = q.NeedingAttendance();
        if (deptId.HasValue)  q = q.Where(u => u.DepartmentId == deptId.Value);
        if (groupId.HasValue) q = q.Where(u => u.AttendanceGroupId == groupId.Value);
        // 分公司管理员范围过滤：deptIds 是"自己范围内的部门 id 全集"（含下级部门），跟上面 deptId 的
        // 单值精确匹配是两码事——deptId 是页面自己选的筛选条件，deptIds 是登录者身份带来的强制范围
        if (deptIds is not null) q = q.Where(u => u.DepartmentId != null && deptIds.Contains(u.DepartmentId.Value));
        return q.Select(u => u.Id);
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
        LateMinutes      = EffectiveLateMinutes(r),
        EarlyLeaveMinutes = EffectiveEarlyLeaveMinutes(r),
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
