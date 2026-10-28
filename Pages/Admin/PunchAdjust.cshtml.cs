using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Exceptions;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 手动补卡页：管理员/文员最高权限——可以直接给任意员工、任意一天补录/修改打卡时间，立即生效，
/// 不用像员工自己提交的"补卡申请"那样走审批流程。用于处理审批流程覆盖不到的特殊情况
/// （如设备故障漏打卡、历史数据补录等，由管理员核实后直接处理）。分公司管理员只能给自己
/// 范围内的员工补卡。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class PunchAdjustModel(IAttendanceService attendanceService, IDeptScopeService deptScopeService, AttendanceDbContext db, ILogger<PunchAdjustModel> logger) : PageModel
{
    [BindProperty] public int      UserId       { get; set; }
    [BindProperty] public DateOnly WorkDate     { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [BindProperty] public int      PunchTypeValue { get; set; } = (int)PunchType.ClockIn;   // 补哪种卡：1 上班 / 2 下班 / 3 午间
    [BindProperty] public string?  PunchTimeText  { get; set; }   // 只填钟点，如 20:05；日期用上面的"考勤日期"
    [BindProperty] public bool     NextDay        { get; set; }   // 勾了 = 这个钟点在考勤日的第二天（夜班下班、过了零点的午间卡）
    [BindProperty] public bool     AllowOverwrite { get; set; }   // 强制覆盖：允许直接改掉已有的上班/下班时间
    [BindProperty] public string?  Remark         { get; set; }

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    /// <summary>操作日志：最近一批"管理员手动补卡"改过的记录，供本页面下方列表展示，方便追溯是谁、什么时候、改了什么。</summary>
    /// <param name="RevertLogId">能撤回时是对应那次补卡的操作记录编号；不能撤回为空。</param>
    /// <param name="RevertHint">不能撤回（旧记录没有补卡前的样子）或已经撤回时，列表里显示的说明。</param>
    public record LogEntry(int UserId, string RealName, string EmployeeNo, DateOnly WorkDate, string ClockInText, string ClockOutText, decimal WorkHours, string? Note, DateTime UpdatedAt,
        int? RevertLogId = null, string? RevertHint = null);
    public List<LogEntry> RecentLog { get; set; } = [];

    public async Task OnGetAsync() => await LoadRecentLogAsync();

    public async Task<IActionResult> OnPostAdjustAsync()
    {
        // WorkDate 是不可为空的 DateOnly，日期框提交空值时模型绑定对非空值类型会静默失败——只在
        // ModelState 里记一条错误，属性本身还是保持字段初始值（今天）。不检查的话，管理员把日期
        // 清空提交，会被无声当成"就是要补今天的卡"处理，而不是提示"请选择日期"。
        if (ModelState.GetFieldValidationState(nameof(WorkDate)) != ModelValidationState.Valid)
        {
            ErrorMessage = "请选择正确的日期";
            await LoadRecentLogAsync();
            return Page();
        }

        try
        {
            if (UserId <= 0) throw new BusinessException("请先选择员工");
            var user = await db.Users.FindAsync(UserId) ?? throw new BusinessException("员工不存在");
            var cuAdj = HttpContext.GetRequiredUser();
            if (!await deptScopeService.CanAccessDeptAsync(cuAdj, user.DepartmentId))
                throw new BusinessException("无权给该员工补卡");
            // 跟员工管理页同一套角色层级：文员不能改管理员的打卡、分公司账号不能改总部账号的打卡（以前只校验了部门范围）
            if (!cuAdj.CanManageAccount(user.Role, user.ScopedDepartmentId))
                throw new BusinessException("无权给该账号补卡，请联系总部管理员操作");

            if (!Enum.IsDefined(typeof(PunchType), PunchTypeValue)) throw new BusinessException("请选择补卡类型");
            var type = (PunchType)PunchTypeValue;
            if (string.IsNullOrWhiteSpace(PunchTimeText) || !TimeOnly.TryParse(PunchTimeText, out var punchClock))
                throw new BusinessException("请填写补卡的打卡时间");
            var punchAt = WorkDate.ToDateTime(punchClock).AddDays(NextDay ? 1 : 0);   // 日期取上面选的考勤日期，不用再选一遍

            var operatorName = User.FindFirstValue("RealName");
            await attendanceService.AdminReplenishPunchAsync(UserId, WorkDate, type, punchAt, AllowOverwrite, Remark, operatorName);
            SuccessMessage = $"已为 {user.RealName}（{user.EmployeeNo}）补录 {WorkDate:yyyy-MM-dd} 的{PunchReplenishmentRules.TypeText(type)}卡（{punchAt:MM-dd HH:mm}），已按班次规则重算工时";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "管理员手动补卡失败，UserId={UserId}", UserId);
            ErrorMessage = AttendanceSystem.Helpers.ErrorReport.Describe(ex, "保存失败，请稍后重试", HttpContext);
        }
        await LoadRecentLogAsync();
        return Page();
    }

    /// <summary>撤回一次手动补卡：恢复成补卡前的样子（上下班时间、状态、工时、午间命中）。</summary>
    public async Task<IActionResult> OnPostRevertAsync(int logId)
    {
        try
        {
            var log = await db.PunchAdjustLogs.FindAsync(logId) ?? throw new BusinessException("找不到这条补卡操作记录");
            var user = await db.Users.FindAsync(log.UserId) ?? throw new BusinessException("员工不存在");
            var cu = HttpContext.GetRequiredUser();
            if (!await deptScopeService.CanAccessDeptAsync(cu, user.DepartmentId))
                throw new BusinessException("无权撤回该员工的补卡");
            if (!cu.CanManageAccount(user.Role, user.ScopedDepartmentId))
                throw new BusinessException("无权撤回该账号的补卡，请联系总部管理员操作");

            await attendanceService.RevertAdminPunchAdjustAsync(logId, User.FindFirstValue("RealName"));
            logger.LogInformation("管理员 {Operator} 撤回了对 {EmployeeNo} {WorkDate} 的手动补卡（操作记录 {LogId}）",
                User.FindFirstValue(ClaimTypes.Name) ?? "?", user.EmployeeNo, log.WorkDate, logId);
            SuccessMessage = $"已撤回 {user.RealName}（{user.EmployeeNo}）{log.WorkDate:yyyy-MM-dd} 的{log.AdjustType}补卡，已恢复成补卡前的样子";
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "撤回手动补卡失败，LogId={LogId}", logId);
            ErrorMessage = AttendanceSystem.Helpers.ErrorReport.Describe(ex, "撤回失败，请稍后重试", HttpContext);
        }
        await LoadRecentLogAsync();
        return Page();
    }

    /// <summary>取最近 50 条"管理员手动补卡"改过的考勤记录（按更新时间倒序），供页面下方的操作记录列表用。</summary>
    private async Task LoadRecentLogAsync()
    {
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetRequiredUser());
        var q = db.AttendanceRecords.Include(r => r.User)
            .Where(r => r.ApprovalNote != null && r.ApprovalNote.Contains("管理员手动补卡"));
        if (visibleIds is not null)
            q = q.Where(r => r.User.DepartmentId != null && visibleIds.Contains(r.User.DepartmentId.Value));
        RecentLog = await q
            .OrderByDescending(r => r.UpdatedAt)
            .Take(50)
            .Select(r => new LogEntry(
                r.UserId, r.User.RealName, r.User.EmployeeNo, r.WorkDate,
                r.ClockInTime  != null ? r.ClockInTime.Value.ToString("MM-dd HH:mm")  : "--",
                r.ClockOutTime != null ? r.ClockOutTime.Value.ToString("MM-dd HH:mm") : "--",
                r.ActualWorkHours, r.ApprovalNote, r.UpdatedAt, null, null))
            .ToListAsync();

        // 每一行配上"撤回"按钮：这天最近一次、还没撤回的手动补卡才有；没有操作记录的旧补卡（功能上线前做的）撤回不了
        var userIds = RecentLog.Select(e => e.UserId).Distinct().ToList();
        var logs = await db.PunchAdjustLogs.Where(l => userIds.Contains(l.UserId)).OrderBy(l => l.Id).ToListAsync();
        var latest = logs.GroupBy(l => (l.UserId, l.WorkDate)).ToDictionary(g => g.Key, g => g.Last());
        RecentLog = RecentLog.Select(e =>
        {
            if (!latest.TryGetValue((e.UserId, e.WorkDate), out var l)) return e with { RevertHint = "旧记录，不能撤回" };
            return l.RevertedAt is not null
                ? e with { RevertHint = $"已撤回（{l.RevertedAt:MM-dd HH:mm}）" }
                : e with { RevertLogId = l.Id };
        }).ToList();
    }

    /// <summary>员工搜索（AJAX）：按姓名/工号模糊匹配，最多返回 20 条，供前端搜索框用。</summary>
    public async Task<JsonResult> OnGetSearchUsersAsync(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new JsonResult(Array.Empty<object>());
        var visibleIds = await deptScopeService.GetVisibleDeptIdsAsync(HttpContext.GetRequiredUser());
        var q = db.Users.Where(u => u.RealName.Contains(keyword) || u.EmployeeNo.Contains(keyword));
        if (visibleIds is not null)
            q = q.Where(u => u.DepartmentId != null && visibleIds.Contains(u.DepartmentId.Value));
        var users = await q
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.RealName)
            .Take(20)
            .Select(u => new { id = u.Id, label = u.RealName + "（" + u.EmployeeNo + "）" + (u.IsActive ? "" : "·已停用") })
            .ToListAsync();
        return new JsonResult(users);
    }

    /// <summary>打卡来源的简短说明：考勤机 / 补卡，其余不标。</summary>
    private static string PunchSourceText(string? deviceInfo) => deviceInfo switch
    {
        null or "" => "",
        var s when s.StartsWith("ZKDevice", StringComparison.Ordinal) => "·考勤机",
        var s when s.StartsWith("Mobile", StringComparison.Ordinal) => "·手机",
        var s when s.StartsWith("补卡", StringComparison.Ordinal) || s.StartsWith("管理员补卡", StringComparison.Ordinal) => "·补卡",
        _ => ""
    };

    /// <summary>查询某员工某天当前的打卡记录（AJAX）：补卡前先看看现状，避免误覆盖。</summary>
    public async Task<JsonResult> OnGetRecordAsync(int userId, DateOnly workDate)
    {
        var targetDeptId = await db.Users.Where(u => u.Id == userId).Select(u => (int?)u.DepartmentId).FirstOrDefaultAsync();
        if (!await deptScopeService.CanAccessDeptAsync(HttpContext.GetRequiredUser(), targetDeptId))
            return new JsonResult(new { exists = false });

        var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == workDate);
        // 班次、午间窗口、这天前后的所有原始打卡：补卡前先看清，免得把午间那次卡当成上班/下班补
        var shift = await db.ShiftAssignments.Where(a => a.UserId == userId && a.WorkDate == workDate)
            .Select(a => a.ShiftSchedule).FirstOrDefaultAsync();
        var from = workDate.ToDateTime(TimeOnly.MinValue);
        // 只列这个考勤日的打卡：白班就是当天；夜班（跨天班次）再加上第二天中午之前（下班卡在那儿），别的日子的打卡不混进来
        var to = shift is { IsCrossDay: true } ? from.AddDays(1).AddHours(12) : from.AddDays(1);
        var punches = (await db.AttendancePunches
                .Where(p => p.UserId == userId && p.PunchTime >= from && p.PunchTime < to)
                .OrderBy(p => p.PunchTime).Select(p => new { p.PunchTime, p.PunchType, p.DeviceInfo }).ToListAsync())
            .Select(p => new
            {
                nextDay = DateOnly.FromDateTime(p.PunchTime) > workDate,
                time = p.PunchTime.ToString("HH:mm"),
                type = PunchReplenishmentRules.TypeText(p.PunchType),
                source = PunchSourceText(p.DeviceInfo).TrimStart('·')
            })
            .ToList();
        // 备注是一条条用"；"连起来的，同样的话（比如反复手动补卡）合并成"×N"，拆成一行一条好读
        var notes = (record?.ApprovalNote ?? "").Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .GroupBy(s => s).Select(g => new { text = g.Key, count = g.Count() }).ToList();
        var shiftText = shift is null ? "未排班"
            : $"{shift.ShiftName} {shift.WorkStartTime:HH\\:mm}-{shift.WorkEndTime:HH\\:mm}"
              + (string.IsNullOrWhiteSpace(shift.MidCheckWindows) ? "，无午间窗口" : $"，午间窗口 {shift.MidCheckWindows.Replace(",", "、")}");
        var crossDay = shift is { IsCrossDay: true };
        var shiftStart = shift?.WorkStartTime.ToString("HH:mm");   // 夜班判断"第二天"用：钟点早于上班时间 = 已经过了零点
        if (record is null) return new JsonResult(new { exists = false, shift = shiftText, punches, crossDay, shiftStart, notes });
        return new JsonResult(new
        {
            shift     = shiftText,
            crossDay,
            shiftStart,
            punches,
            midResult = record.MidCheckResults,
            notes,
            exists    = true,
            clockIn   = record.ClockInTime?.ToString("yyyy-MM-ddTHH:mm"),
            clockOut  = record.ClockOutTime?.ToString("yyyy-MM-ddTHH:mm"),
            status    = AttendanceService.StatusText(record.AttendanceStatus),
            workHours = record.ActualWorkHours,
            note      = record.ApprovalNote   // 旧字段，页面改用 notes（已拆开去重）
        });
    }
}
