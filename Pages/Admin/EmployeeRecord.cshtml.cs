using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 员工记录查询页：输入员工姓名（或工号）就能一页看到这个人的全部考勤相关信息——基本信息、考勤组、排班（包括哪天没排班）、
/// 考勤记录、原始打卡、审批单，并把"没有考勤组/没排班/有打卡但没排班/连续旷工"这些异常直接标出来（2026-10-21 新增：
/// 查一个人以前要翻好几个页面、还得连着查数据库）。只读页面；分公司管理员只能查自己范围内的员工。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class EmployeeRecordModel(AttendanceDbContext db, IDeptScopeService deptScopeService, TimeProvider timeProvider) : PageModel
{
    /// <summary>默认查"今天往前 30 天"的考勤记录/打卡/审批；排班窗口则是"往前 14 天、往后 45 天"，方便看出排班有没有断档。</summary>
    private const int DefaultBackDays = 30, ShiftBackDays = 14, ShiftAheadDays = 45;

    public string? Keyword { get; set; }
    public int? UserId { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public DateOnly Today { get; set; }

    public record Match(int Id, string EmployeeNo, string RealName, string? Dept, string? Group, bool Active);
    public List<Match> Matches { get; set; } = [];

    public record Info(int Id, string EmployeeNo, string RealName, string? Dept, string? Position, string? GroupName, DateOnly? HourlyStart,
        string RoleText, bool Active, bool Exempt, DateOnly? HireDate, string? Supervisor, string? Phone, string? Devices);
    public Info? Emp { get; set; }

    public record ShiftDay(DateOnly Date, string? ShiftName, string? Time, bool IsRest, bool CrossDay, bool Missing, bool HasPunch);
    public List<ShiftDay> Shifts { get; set; } = [];

    public record Rec(DateOnly Date, string? ShiftName, DateTime? In, DateTime? Out, decimal Work, decimal Overtime, decimal Leave,
        string Status, int LateMinutes, string? Mid, string? Note, bool NoShift);
    public List<Rec> Records { get; set; } = [];
    public decimal TotalWork { get; set; }
    public decimal TotalOvertime { get; set; }

    public record PunchRow(DateTime Time, string Type, string Source, string? Address, bool Valid);
    public List<PunchRow> Punches { get; set; } = [];

    public record ApprovalRow(string No, string Type, string Status, string Time, decimal? Hours, DateTime SubmittedAt);
    public List<ApprovalRow> Approvals { get; set; } = [];

    /// <summary>页面顶部要提醒管理员注意的异常（红/黄色提示条）。</summary>
    public record Flag(string Level, string Text);
    public List<Flag> Flags { get; set; } = [];

    public string? Message { get; set; }

    public async Task OnGetAsync(string? keyword, int? id, DateOnly? start, DateOnly? end)
    {
        Today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        End = end ?? Today;
        Start = start ?? Today.AddDays(-DefaultBackDays);
        if (End < Start) (Start, End) = (End, Start);
        if (End.DayNumber - Start.DayNumber > 180) Start = End.AddDays(-180);   // 防止选超长区间撑爆页面
        Keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();

        var cu = HttpContext.GetRequiredUser();
        var visibleDepts = await deptScopeService.GetVisibleDeptIdsAsync(cu);   // null = 不限范围

        // 1) 按姓名/工号找人（重名的都列出来，让管理员自己点）
        if (Keyword is not null)
        {
            var mq = db.Users.Where(u => u.RealName.Contains(Keyword) || u.EmployeeNo.Contains(Keyword));
            if (visibleDepts is not null) mq = mq.Where(u => u.DepartmentId.HasValue && visibleDepts.Contains(u.DepartmentId.Value));
            Matches = await mq.OrderByDescending(u => u.IsActive).ThenBy(u => u.EmployeeNo).Take(30)
                .Select(u => new Match(u.Id, u.EmployeeNo, u.RealName,
                    u.Department != null ? u.Department.DeptName : null,
                    u.AttendanceGroup != null ? u.AttendanceGroup.GroupName : null, u.IsActive))
                .ToListAsync();
            if (id is null && Matches.Count == 1) id = Matches[0].Id;   // 只有一个人就直接展开
        }
        if (id is null) return;

        // 2) 目标员工：必须在自己范围内（id 是 URL 参数，受限管理员可以手改，这里要单独校验）
        var user = await db.Users.Include(u => u.Department).Include(u => u.AttendanceGroup).FirstOrDefaultAsync(u => u.Id == id.Value);
        if (user is null || !await deptScopeService.CanAccessDeptAsync(cu, user.DepartmentId))
        {
            Message = "没有找到这个员工，或不在您的管理范围内";
            return;
        }
        UserId = user.Id;
        var supervisor = user.SupervisorUserId is { } sid ? await db.Users.Where(u => u.Id == sid).Select(u => u.RealName).FirstOrDefaultAsync() : null;
        var devices = await db.UserZKDevices.Where(m => m.UserId == user.Id).Select(m => m.ZKDevice.SN).ToListAsync();
        Emp = new Info(user.Id, user.EmployeeNo, user.RealName, user.Department?.DeptName, user.Position, user.AttendanceGroup?.GroupName,
            user.AttendanceGroup?.HourlyWorkStartDate,
            user.Role switch { UserRole.Employee => "普通员工（临时工）", UserRole.Admin => "管理员", _ => user.Role.ToString() },
            user.IsActive, user.IsExemptFromAttendance(), user.HireDate, supervisor, user.Phone,
            devices.Count == 0 ? null : string.Join("、", devices));

        await LoadDetailsAsync(user);
    }

    private async Task LoadDetailsAsync(User user)
    {
        var uid = user.Id;
        var startDt = Start.ToDateTime(TimeOnly.MinValue);
        var endDt = End.AddDays(1).ToDateTime(TimeOnly.MinValue);

        // ── 排班：窗口 = 查询区间 ∪ [今天-14, 今天+45]，缺的日子标红 ──
        var shiftFrom = Start < Today.AddDays(-ShiftBackDays) ? Start : Today.AddDays(-ShiftBackDays);
        var shiftTo = End > Today.AddDays(ShiftAheadDays) ? End : Today.AddDays(ShiftAheadDays);
        var assigns = (await db.ShiftAssignments.Where(a => a.UserId == uid && a.WorkDate >= shiftFrom && a.WorkDate <= shiftTo)
                .Select(a => new { a.WorkDate, a.ShiftSchedule }).ToListAsync())
            .ToDictionary(a => a.WorkDate, a => a.ShiftSchedule);

        var records = await db.AttendanceRecords.Where(r => r.UserId == uid && r.WorkDate >= Start && r.WorkDate <= End)
            .OrderBy(r => r.WorkDate).ToListAsync();
        var recordDays = records.Where(r => r.ClockInTime != null).Select(r => r.WorkDate).ToHashSet();

        var hireDay = user.HireDate;
        var needsShift = user.IsActive && !user.IsExemptFromAttendance();
        for (var d = shiftFrom; d <= shiftTo; d = d.AddDays(1))
        {
            assigns.TryGetValue(d, out var s);
            var missing = needsShift && s is null && (hireDay is null || d >= hireDay);   // 免考勤/已停用的人不需要排班，不标红
            Shifts.Add(new ShiftDay(d, s?.ShiftName, s is null ? null : $"{s.WorkStartTime:HH\\:mm}–{s.WorkEndTime:HH\\:mm}",
                s is not null && AttendanceService.IsShiftWeeklyRestDay(d, s), s?.IsCrossDay ?? false, missing, recordDays.Contains(d)));
        }

        // ── 考勤记录 ──
        foreach (var r in records)
        {
            assigns.TryGetValue(r.WorkDate, out var s);
            Records.Add(new Rec(r.WorkDate, s?.ShiftName, r.ClockInTime, r.ClockOutTime, r.ActualWorkHours, r.OvertimeHours, r.LeaveHours,
                StatusText(r.AttendanceStatus), r.LateMinutes, r.MidCheckResults,
                string.Join("；", new[] { r.ApprovalNote, r.Remark }.Where(x => !string.IsNullOrWhiteSpace(x))), s is null && r.ClockInTime != null));
        }
        TotalWork = records.Sum(r => r.ActualWorkHours);
        TotalOvertime = records.Sum(r => r.OvertimeHours);

        // ── 原始打卡 ──
        Punches = (await db.AttendancePunches.Where(p => p.UserId == uid && p.PunchTime >= startDt && p.PunchTime < endDt)
                .OrderBy(p => p.PunchTime).Take(2000).ToListAsync())
            .Select(p => new PunchRow(p.PunchTime, p.PunchType switch { PunchType.ClockIn => "上班", PunchType.ClockOut => "下班", _ => "午间" },
                SourceText(p.DeviceInfo), p.Address, p.IsValid)).ToList();

        // ── 审批单：区间内提交的 + 加班/请假时间落在区间内的 ──
        var approvals = await db.ApprovalRequests.Where(a => a.ApplicantUserId == uid
                && ((a.SubmittedAt >= startDt && a.SubmittedAt < endDt)
                    || (a.OvertimeStartTime != null && a.OvertimeStartTime >= startDt && a.OvertimeStartTime < endDt)
                    || (a.LeaveStartTime != null && a.LeaveStartTime >= startDt && a.LeaveStartTime < endDt)))
            .OrderByDescending(a => a.SubmittedAt).Take(200).ToListAsync();
        Approvals = approvals.Select(a => new ApprovalRow(a.RequestNo,
            a.ApprovalType switch { ApprovalType.PunchReplenishment => "补卡", ApprovalType.Leave => "请假", ApprovalType.Overtime => "加班", _ => "出差" },
            a.ApprovalStatus switch { ApprovalStatus.Pending => "待审批", ApprovalStatus.InProgress => "审批中", ApprovalStatus.Approved => "已通过", ApprovalStatus.Rejected => "已驳回", _ => "已撤销" },
            a.ApprovalType switch
            {
                ApprovalType.Overtime => $"{a.OvertimeStartTime:MM-dd HH:mm} ~ {a.OvertimeEndTime:MM-dd HH:mm}",
                ApprovalType.Leave => $"{a.LeaveStartTime:MM-dd HH:mm} ~ {a.LeaveEndTime:MM-dd HH:mm}",
                ApprovalType.BusinessTrip => $"{a.BusinessTripStartTime:MM-dd HH:mm} ~ {a.BusinessTripEndTime:MM-dd HH:mm}",
                _ => $"{a.PunchDate:MM-dd} {a.PunchTime:HH\\:mm}"
            },
            a.ApprovalType == ApprovalType.Overtime ? a.OvertimeDurationHours : a.ApprovalType == ApprovalType.Leave ? a.LeaveDurationHours : null,
            a.SubmittedAt)).ToList();

        // ── 异常提示 ──
        var lastPunch = await db.AttendancePunches.Where(p => p.UserId == uid && p.IsValid).MaxAsync(p => (DateTime?)p.PunchTime);
        var hourly = Emp?.HourlyStart is { } hs && hs <= Today;
        if (user.AttendanceGroupId is null) Flags.Add(new Flag("danger", "没有考勤组：不会排班、不会算工时，每个工作日还会被记旷工。请到「员工管理」里给他分配考勤组。"));
        if (!user.IsActive) Flags.Add(new Flag("secondary", "账号已停用。"));
        if (Emp?.Exempt == true) Flags.Add(new Flag("secondary", "免考勤（正式工角色或勾了免考勤）：不打卡、不记旷工、不进报表。"));
        if (hourly) Flags.Add(new Flag("info", $"计时工模式（{Emp!.HourlyStart:yyyy-MM-dd} 起）：工时按实际打卡时长算，没有加班、休息日、旷工、早退。"));

        var lastAssign = assigns.Keys.Any() ? assigns.Keys.Max() : (DateOnly?)null;
        var hasFutureAssign = assigns.Keys.Any(k => k >= Today);
        if (user.IsActive && Emp?.Exempt != true)
        {
            if (!hasFutureAssign)
                Flags.Add(new Flag("danger", $"今天起没有排班（最后一次排班：{(lastAssign is null ? "从来没有" : lastAssign.Value.ToString("yyyy-MM-dd"))}）。没排班时加班检查、午间窗口、迟到早退都不准，请到「排班管理」补排。"));
            else if (lastAssign < Today.AddDays(7))
                Flags.Add(new Flag("warning", $"排班只到 {lastAssign:yyyy-MM-dd}，快到期了，请续排。"));
        }
        var punchedNoShift = Shifts.Where(s => s.Date >= Start && s.Date <= End && s.Missing && s.HasPunch).Select(s => s.Date).ToList();
        if (punchedNoShift.Count > 0)
            Flags.Add(new Flag("danger", $"有 {punchedNoShift.Count} 天有打卡但当天没排班（{string.Join("、", punchedNoShift.Take(8).Select(d => d.ToString("MM-dd")))}{(punchedNoShift.Count > 8 ? " …" : "")}）：这些天的工时/加班没有按班次校验，补排班后系统会自动对账加班。"));
        // 排班类型和实际打卡时间对不上：夜班排班但上班卡在白天，或白班排班但上班卡在深夜——多半是排错了班次
        // （批量排班会覆盖已有排班），夜班计时工的白天打卡会被当成无效，工时也会按夜班规则算
        var mismatch = records.Where(r => r.ClockInTime is { } ci && assigns.TryGetValue(r.WorkDate, out var sh)
                && ((sh.IsCrossDay && ci.Hour is >= 7 and < 17) || (!sh.IsCrossDay && ci.Hour >= 22))).Select(r => r.WorkDate).ToList();
        if (mismatch.Count > 0)
            Flags.Add(new Flag("danger", $"有 {mismatch.Count} 天的打卡时间和排班类型对不上（{string.Join("、", mismatch.Take(8).Select(d => d.ToString("MM-dd")))}{(mismatch.Count > 8 ? " …" : "")}）：夜班排班却在白天上班打卡（或白班排班却深夜打卡），多半是排错了班次，工时可能不准，请核对排班。"));
        var absent = records.Count(r => r.AttendanceStatus == AttendanceStatus.Absent);
        if (absent >= 3) Flags.Add(new Flag("danger", $"所选区间内旷工 {absent} 天。"));
        var late = records.Count(r => r.AttendanceStatus == AttendanceStatus.Late);
        if (late >= 3) Flags.Add(new Flag("warning", $"所选区间内迟到 {late} 次（共 {records.Where(r => r.AttendanceStatus == AttendanceStatus.Late).Sum(r => r.LateMinutes)} 分钟）。"));
        if (lastPunch is null && needsShift) Flags.Add(new Flag("warning", "从来没有打过卡。"));
        else if (lastPunch is not null && needsShift && lastPunch < Today.AddDays(-7).ToDateTime(TimeOnly.MinValue))
            Flags.Add(new Flag("warning", $"最后一次打卡是 {lastPunch:yyyy-MM-dd HH:mm}，已经超过 7 天没打卡。"));
    }

    private static string StatusText(AttendanceStatus s) => s switch
    {
        AttendanceStatus.Normal => "正常", AttendanceStatus.Late => "迟到", AttendanceStatus.EarlyLeave => "早退", AttendanceStatus.Absent => "旷工",
        AttendanceStatus.Holiday => "节假日", AttendanceStatus.OnLeave => "请假", AttendanceStatus.Overtime => "加班", AttendanceStatus.NotPunched => "未打卡",
        AttendanceStatus.BusinessTrip => "出差", _ => s.ToString()
    };

    private static string SourceText(string? deviceInfo)
    {
        if (string.IsNullOrWhiteSpace(deviceInfo)) return "";
        if (deviceInfo.StartsWith("ZKDevice:", StringComparison.OrdinalIgnoreCase)) return "考勤机";
        if (deviceInfo.StartsWith("MobileFace", StringComparison.OrdinalIgnoreCase)) return "手机人脸";
        if (deviceInfo.StartsWith("管理员补卡", StringComparison.Ordinal)) return "管理员补卡";
        if (deviceInfo.StartsWith("补卡审批", StringComparison.Ordinal)) return "员工补卡";
        return "其他";
    }
}
