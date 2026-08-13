using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 手动补卡页：管理员/文员最高权限——可以直接给任意员工、任意一天补录/修改打卡时间，立即生效，
/// 不用像员工自己提交的"补卡申请"那样走审批流程。用于处理审批流程覆盖不到的特殊情况
/// （如设备故障漏打卡、历史数据补录等，由管理员核实后直接处理）。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class PunchAdjustModel(IAttendanceService attendanceService, AttendanceDbContext db) : PageModel
{
    [BindProperty] public int      UserId       { get; set; }
    [BindProperty] public DateOnly WorkDate     { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    [BindProperty] public string?  ClockInTime  { get; set; }   // datetime-local 字符串，如 2026-08-13T20:05
    [BindProperty] public string?  ClockOutTime { get; set; }
    [BindProperty] public string?  Remark       { get; set; }

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage   { get; set; }

    /// <summary>操作日志：最近一批"管理员手动补卡"改过的记录，供本页面下方列表展示，方便追溯是谁、什么时候、改了什么。</summary>
    public record LogEntry(string RealName, string EmployeeNo, DateOnly WorkDate, string ClockInText, string ClockOutText, decimal WorkHours, string? Note, DateTime UpdatedAt);
    public List<LogEntry> RecentLog { get; set; } = [];

    public async Task OnGetAsync() => await LoadRecentLogAsync();

    public async Task<IActionResult> OnPostAdjustAsync()
    {
        try
        {
            if (UserId <= 0) throw new InvalidOperationException("请先选择员工");
            var user = await db.Users.FindAsync(UserId) ?? throw new InvalidOperationException("员工不存在");

            var clockIn  = string.IsNullOrWhiteSpace(ClockInTime)  ? (DateTime?)null : DateTime.Parse(ClockInTime);
            var clockOut = string.IsNullOrWhiteSpace(ClockOutTime) ? (DateTime?)null : DateTime.Parse(ClockOutTime);
            if (clockIn is null && clockOut is null)
                throw new InvalidOperationException("上班/下班打卡时间至少要填一个");

            var operatorName = User.FindFirstValue("RealName");
            await attendanceService.AdminAdjustPunchAsync(UserId, WorkDate, clockIn, clockOut, Remark, operatorName);
            SuccessMessage = $"已为 {user.RealName}（{user.EmployeeNo}）补录 {WorkDate:yyyy-MM-dd} 的打卡记录";
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await LoadRecentLogAsync();
        return Page();
    }

    /// <summary>取最近 50 条"管理员手动补卡"改过的考勤记录（按更新时间倒序），供页面下方的操作记录列表用。</summary>
    private async Task LoadRecentLogAsync()
    {
        RecentLog = await db.AttendanceRecords
            .Include(r => r.User)
            .Where(r => r.ApprovalNote != null && r.ApprovalNote.StartsWith("管理员手动补卡"))
            .OrderByDescending(r => r.UpdatedAt)
            .Take(50)
            .Select(r => new LogEntry(
                r.User.RealName, r.User.EmployeeNo, r.WorkDate,
                r.ClockInTime  != null ? r.ClockInTime.Value.ToString("MM-dd HH:mm")  : "--",
                r.ClockOutTime != null ? r.ClockOutTime.Value.ToString("MM-dd HH:mm") : "--",
                r.ActualWorkHours, r.ApprovalNote, r.UpdatedAt))
            .ToListAsync();
    }

    /// <summary>员工搜索（AJAX）：按姓名/工号模糊匹配，最多返回 20 条，供前端搜索框用。</summary>
    public async Task<JsonResult> OnGetSearchUsersAsync(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new JsonResult(Array.Empty<object>());
        var users = await db.Users
            .Where(u => u.RealName.Contains(keyword) || u.EmployeeNo.Contains(keyword))
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.RealName)
            .Take(20)
            .Select(u => new { id = u.Id, label = u.RealName + "（" + u.EmployeeNo + "）" + (u.IsActive ? "" : "·已停用") })
            .ToListAsync();
        return new JsonResult(users);
    }

    /// <summary>查询某员工某天当前的打卡记录（AJAX）：补卡前先看看现状，避免误覆盖。</summary>
    public async Task<JsonResult> OnGetRecordAsync(int userId, DateOnly workDate)
    {
        var record = await db.AttendanceRecords.FirstOrDefaultAsync(r => r.UserId == userId && r.WorkDate == workDate);
        if (record is null) return new JsonResult(new { exists = false });
        return new JsonResult(new
        {
            exists    = true,
            clockIn   = record.ClockInTime?.ToString("yyyy-MM-ddTHH:mm"),
            clockOut  = record.ClockOutTime?.ToString("yyyy-MM-ddTHH:mm"),
            status    = AttendanceService.StatusText(record.AttendanceStatus),
            workHours = record.ActualWorkHours,
            note      = record.ApprovalNote
        });
    }
}
