using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Pages.Admin;

/// <summary>
/// 班次与排班管理页：
/// ● 支持「多选考勤组」→ 合并这些组的成员；
/// ● 可在合并名单里「跨组勾选」部分人，一起排到同一个班次+日期区间（工作调动/混排）。
/// </summary>
[Authorize(Policy = "ManagePolicy")]
public class ShiftManageModel(AttendanceDbContext db) : PageModel
{
    public List<AttendanceGroup> Groups           { get; set; } = [];   // 所有考勤组（多选用）
    public List<int>             SelectedGroupIds  { get; set; } = [];   // 当前选中的考勤组
    public List<ShiftSchedule>   Shifts            { get; set; } = [];   // 选中组的班次
    public List<MemberRow>       Members           { get; set; } = [];   // 选中组的合并成员（跨组）
    public List<AssignmentRow>   Assignments       { get; set; } = [];

    public DateOnly ViewStart { get; set; }
    public DateOnly ViewEnd   { get; set; }

    /// <summary>合并成员的一行：某人属于哪个组、哪个部门（部门用于批量排班时按部门筛选，一个考勤组可能横跨多个部门）。</summary>
    public record MemberRow(int UserId, string RealName, string EmployeeNo, int GroupId, string GroupName, string? DeptName);

    /// <summary>一行排班展示：某天某班次有哪些人。</summary>
    public record AssignmentRow(DateOnly WorkDate, string ShiftName, string Color, List<string> People)
    {
        public string WeekText => WorkDate.DayOfWeek switch
        {
            DayOfWeek.Monday    => "周一", DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday  => "周四", DayOfWeek.Friday  => "周五", DayOfWeek.Saturday  => "周六",
            _                   => "周日"
        };
    }

    [TempData] public string? SuccessMessage { get; set; }
    [TempData] public string? ErrorMessage   { get; set; }

    // ── 班次表单字段 ──
    [BindProperty] public int     ShiftId    { get; set; }
    [BindProperty] public int     GroupId    { get; set; }   // 班次归属的考勤组
    [BindProperty] public string  ShiftName  { get; set; } = "";
    [BindProperty] public string  WorkStart  { get; set; } = "09:00";
    [BindProperty] public string  WorkEnd    { get; set; } = "18:00";
    [BindProperty] public int     LateTol    { get; set; } = 5;
    [BindProperty] public int     EarlyTol   { get; set; } = 5;
    [BindProperty] public int     EarliestIn { get; set; } = 60;
    [BindProperty] public int     OtThresh   { get; set; } = 30;
    [BindProperty] public bool    CrossDay   { get; set; }
    [BindProperty] public decimal StdHours   { get; set; } = 8;
    [BindProperty] public string  ShiftColor { get; set; } = "#1890ff";
    /// <summary>每周休息日（勾选的星期几，0=周日...6=周六）</summary>
    [BindProperty] public List<int> RestDays { get; set; } = [];

    /// <summary>班中必打卡窗口表单一行输入（开始/结束时间字符串）。</summary>
    public class MidCheckWindowInput
    {
        public string? Start { get; set; }
        public string? End   { get; set; }
    }

    /// <summary>
    /// 班中必打卡窗口列表（可以配多段，比如上午茶歇 + 午休各一段；两个都留空的行会被跳过）：
    /// 不同班次时间段不一样，需要管理员按各班次的实际上下班时间自己设置
    /// （比如 8-18 点的班配 12-13 点；下午班/晚班配对应中段的时间）。
    /// </summary>
    [BindProperty] public List<MidCheckWindowInput> MidCheckWindowInputs { get; set; } = [];

    // ── 排班表单字段 ──
    [BindProperty] public int       AssignShiftId { get; set; }        // 排哪个班次
    [BindProperty] public string    AssignStart   { get; set; } = "";  // 起始日期
    [BindProperty] public string    AssignEnd     { get; set; } = "";  // 结束日期
    [BindProperty] public List<int> AssignUserIds { get; set; } = [];  // 勾选的员工（可跨组）
    [BindProperty] public bool      AssignAll     { get; set; }        // 是否所选组全部成员

    /// <summary>选中的考勤组上下文（随各表单回传，提交后仍保持在同一批组）。</summary>
    [BindProperty] public List<int> CtxGroupIds { get; set; } = [];

    /// <summary>打开页面：加载考勤组、选中组的班次/合并成员/排班记录。</summary>
    public async Task OnGetAsync(int[]? groupIds = null, string? viewStart = null, string? viewEnd = null)
    {
        Groups = await db.AttendanceGroups.Where(g => g.IsActive).OrderBy(g => g.GroupName).ToListAsync();

        SelectedGroupIds = (groupIds is { Length: > 0 })
            ? groupIds.Where(id => id > 0).Distinct().ToList()
            : (Groups.FirstOrDefault() is { } fg ? [fg.Id] : []);

        var today = DateOnly.FromDateTime(DateTime.Today);
        ViewStart = DateOnly.TryParse(viewStart, out var vs) ? vs : today;
        ViewEnd   = DateOnly.TryParse(viewEnd,   out var ve) ? ve : today.AddDays(30);
        if (ViewEnd < ViewStart) ViewEnd = ViewStart;

        if (SelectedGroupIds.Count == 0) return;

        // 选中组的班次（带组名，供排班下拉和班次列表显示）
        Shifts = await db.ShiftSchedules
            .Include(s => s.AttendanceGroup)
            .Where(s => SelectedGroupIds.Contains(s.AttendanceGroupId) && s.IsActive)
            .OrderBy(s => s.AttendanceGroup.GroupName).ThenBy(s => s.ShiftName)
            .ToListAsync();

        // 选中组的合并成员（跨组）；带上部门名，方便批量排班时按部门筛选
        Members = await db.Users
            .Include(u => u.AttendanceGroup)
            .Include(u => u.Department)
            .Where(u => u.IsActive && u.AttendanceGroupId != null && SelectedGroupIds.Contains(u.AttendanceGroupId.Value))
            .OrderBy(u => u.AttendanceGroup!.GroupName).ThenBy(u => u.RealName)
            .Select(u => new MemberRow(u.Id, u.RealName, u.EmployeeNo, u.AttendanceGroupId!.Value, u.AttendanceGroup!.GroupName, u.Department != null ? u.Department.DeptName : null))
            .ToListAsync();

        // 展示窗口内的排班记录（选中组成员的），按「日期 + 班次」聚合
        var rows = await db.ShiftAssignments
            .Where(a => a.WorkDate >= ViewStart && a.WorkDate <= ViewEnd
                     && a.User.IsActive && a.User.AttendanceGroupId != null
                     && SelectedGroupIds.Contains(a.User.AttendanceGroupId.Value))
            .Select(a => new { a.WorkDate, a.ShiftSchedule.ShiftName, a.ShiftSchedule.Color, a.User.RealName })
            .ToListAsync();

        Assignments = rows
            .GroupBy(r => new { r.WorkDate, r.ShiftName, r.Color })
            .OrderBy(g => g.Key.WorkDate).ThenBy(g => g.Key.ShiftName)
            .Select(g => new AssignmentRow(g.Key.WorkDate, g.Key.ShiftName, g.Key.Color,
                g.Select(x => x.RealName).OrderBy(n => n).ToList()))
            .ToList();
    }

    /// <summary>
    /// 导出"排班记录"：所选组每个人在所选日期区间里，每天排的是哪个班次，没排班就留空并计入
    /// "漏排班天数"（方便一眼看出有没有人漏排）。休息日/法定节假日（非调班补班）不算漏排——
    /// 这两种"该不该算漏排"的判断口径跟 <see cref="AttendanceService.GenerateTemplateReportAsync"/>
    /// 里"没排班时按全局周末兜底"的逻辑保持一致：没有排班就不知道这个人具体哪天该休，
    /// 只能退一步按公司通用的"周六周日 + 假期表"来判断，不是真的按每个人的班次休息日精确算。
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync(int[]? groupIds, string? viewStart, string? viewEnd)
    {
        var selectedGroupIds = (groupIds is { Length: > 0 }) ? groupIds.Where(id => id > 0).Distinct().ToList() : [];
        if (selectedGroupIds.Count == 0) { ErrorMessage = "请先勾选至少一个考勤组"; return RedirectToSelf(viewStart, viewEnd, selectedGroupIds); }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = DateOnly.TryParse(viewStart, out var vs) ? vs : today;
        var end   = DateOnly.TryParse(viewEnd,   out var ve) ? ve : today.AddDays(30);
        if (end < start) end = start;
        if (end.DayNumber - start.DayNumber > 366) { ErrorMessage = "导出的日期范围不能超过 366 天"; return RedirectToSelf(viewStart, viewEnd, selectedGroupIds); }

        var dates = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1)) dates.Add(d);

        var members = await db.Users
            .Include(u => u.AttendanceGroup)
            .Include(u => u.Department)
            .Where(u => u.IsActive && u.AttendanceGroupId != null && selectedGroupIds.Contains(u.AttendanceGroupId.Value))
            .OrderBy(u => u.AttendanceGroup!.GroupName).ThenBy(u => u.RealName)
            .ToListAsync();
        if (members.Count == 0) { ErrorMessage = "所选考勤组内没有在职员工"; return RedirectToSelf(viewStart, viewEnd, selectedGroupIds); }

        var userIds = members.Select(u => u.Id).ToList();
        var assignByUser = (await db.ShiftAssignments
                .Include(a => a.ShiftSchedule)
                .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= start && a.WorkDate <= end)
                .ToListAsync())
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.WorkDate));

        var holidays = await db.Holidays
            .Where(h => h.HolidayDate >= start && h.HolidayDate <= end
                     && (h.AttendanceGroupId == null || selectedGroupIds.Contains(h.AttendanceGroupId.Value)))
            .ToListAsync();

        var rows = new List<(string RealName, string EmployeeNo, string GroupName, string? DeptName, List<string?> DailyShiftName, List<bool> DailyMissing, int MissingCount)>();
        foreach (var user in members)
        {
            var assignByDate = assignByUser.GetValueOrDefault(user.Id) ?? new Dictionary<DateOnly, ShiftAssignment>();
            var myHolidays   = holidays.Where(h => h.AttendanceGroupId == null || h.AttendanceGroupId == user.AttendanceGroupId).ToList();

            var dailyShiftName = new List<string?>();
            var dailyMissing   = new List<bool>();
            var missingCount = 0;
            foreach (var date in dates)
            {
                if (user.HireDate is { } hireDate && date < hireDate)
                {
                    dailyShiftName.Add(null);   // 还没入职，不算漏排
                    dailyMissing.Add(false);
                    continue;
                }
                if (assignByDate.TryGetValue(date, out var assign))
                {
                    dailyShiftName.Add(assign.ShiftSchedule.ShiftName);
                    dailyMissing.Add(false);
                    continue;
                }

                var holiday      = myHolidays.FirstOrDefault(h => h.HolidayDate == date);
                var isCompDay    = holiday?.HolidayType == HolidayType.CompensatoryWorkDay;
                var isHolidayOff = holiday is not null && !isCompDay;
                var isWeekendOff = !isCompDay && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

                dailyShiftName.Add(null);
                if (isHolidayOff || isWeekendOff)
                {
                    dailyMissing.Add(false);   // 休息日/节假日，不算漏排
                }
                else
                {
                    dailyMissing.Add(true);    // 正常工作日却没排班 → 漏排，格子会标红
                    missingCount++;
                }
            }

            rows.Add((user.RealName, user.EmployeeNo, user.AttendanceGroup!.GroupName,
                user.Department?.DeptName, dailyShiftName, dailyMissing, missingCount));
        }

        var bytes = ExcelExportHelper.ExportShiftAssignments(dates, rows);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"排班记录_{start:yyyyMMdd}-{end:yyyyMMdd}.xlsx");
    }

    /// <summary>保存班次（新增/修改）。班次归属所选的某个考勤组。</summary>
    public async Task<IActionResult> OnPostSaveShiftAsync()
    {
        try
        {
            if (GroupId == 0) throw new Exception("请选择该班次所属的考勤组");
            // 先判断原始值是否为空，再 Trim：字段留空提交时模型绑定会把它转成 null，直接 Trim() 会抛空引用异常
            if (string.IsNullOrWhiteSpace(ShiftName)) throw new Exception("请填写班次名称");
            var name = ShiftName.Trim();
            if (name.Length > 50) throw new Exception("班次名称不能超过 50 个字");
            if (!TimeOnly.TryParse(WorkStart, out var ws)) throw new Exception("上班时间格式不正确");
            if (!TimeOnly.TryParse(WorkEnd, out var we)) throw new Exception("下班时间格式不正确");
            if (!CrossDay && we == ws) throw new Exception("下班时间不能和上班时间相同（如果是跨天班次，请勾选「跨天」）");
            if (LateTol is < 0 or > 60) throw new Exception("迟到容忍分钟数请填 0-60 之间");
            if (EarlyTol is < 0 or > 60) throw new Exception("早退容忍分钟数请填 0-60 之间");
            if (EarliestIn < 0) throw new Exception("最多提前打卡分钟数不能为负数");
            if (OtThresh is < 0 or > 120) throw new Exception("加班判定阈值请填 0-120 分钟之间");
            if (StdHours is <= 0 or > 24) throw new Exception("标准工时请填 0-24 小时之间");
            if (!System.Text.RegularExpressions.Regex.IsMatch(ShiftColor, "^#[0-9A-Fa-f]{6}$"))
                throw new Exception("班次颜色格式不正确");

            // 午间必打卡窗口：每一行两个都留空就跳过；填了就必须成对、且开始早于结束
            // （不同班次时间不一样，由管理员按这个班次自己的上下班时间去设置对应的中段窗口，
            //  比如 8-18 点的班配 12-13 点；下午班/晚班配各自班次中段的时间；可以配多段）
            var midCheckWindows = new List<(TimeOnly Start, TimeOnly End)>();
            foreach (var w in MidCheckWindowInputs)
            {
                var hasStart = !string.IsNullOrWhiteSpace(w.Start);
                var hasEnd   = !string.IsNullOrWhiteSpace(w.End);
                if (!hasStart && !hasEnd) continue;
                if (!hasStart || !hasEnd) throw new Exception("午间必打卡窗口要么都填，要么都留空");
                if (!TimeOnly.TryParse(w.Start, out var ms) || !TimeOnly.TryParse(w.End, out var me))
                    throw new Exception("午间必打卡窗口时间格式不正确");
                if (me <= ms) throw new Exception("午间必打卡窗口的结束时间要晚于开始时间");
                midCheckWindows.Add((ms, me));
            }
            var midCheckWindowsCsv = midCheckWindows.FormatMidCheckWindows();

            // 把勾选的星期几（0=周日...6=周六）拼成逗号分隔的字符串存起来
            var restDaysCsv = string.Join(",", RestDays.Where(d => d is >= 0 and <= 6).Distinct().OrderBy(d => d));

            if (ShiftId == 0)   // 新增
            {
                db.ShiftSchedules.Add(new ShiftSchedule
                {
                    AttendanceGroupId          = GroupId,
                    ShiftName                  = ShiftName.Trim(),
                    WorkStartTime              = ws,
                    WorkEndTime                = we,
                    LateToleranceMinutes       = LateTol,
                    EarlyLeaveToleranceMinutes = EarlyTol,
                    EarliestClockInMinutes     = EarliestIn,
                    OvertimeThresholdMinutes   = OtThresh,
                    IsCrossDay                 = CrossDay,
                    StandardWorkHours          = StdHours,
                    Color                      = ShiftColor,
                    RestDaysOfWeek             = restDaysCsv,
                    MidCheckWindows            = midCheckWindowsCsv,
                    IsActive                   = true,
                    CreatedAt                  = DateTime.Now,
                    UpdatedAt                  = DateTime.Now
                });
                SuccessMessage = $"班次「{ShiftName}」已添加";
            }
            else   // 修改
            {
                var s = await db.ShiftSchedules.FindAsync(ShiftId);
                if (s is not null)
                {
                    s.AttendanceGroupId          = GroupId;
                    s.ShiftName                  = ShiftName.Trim();
                    s.WorkStartTime              = ws;
                    s.WorkEndTime                = we;
                    s.LateToleranceMinutes       = LateTol;
                    s.EarlyLeaveToleranceMinutes = EarlyTol;
                    s.EarliestClockInMinutes     = EarliestIn;
                    s.OvertimeThresholdMinutes   = OtThresh;
                    s.IsCrossDay                 = CrossDay;
                    s.StandardWorkHours          = StdHours;
                    s.Color                      = ShiftColor;
                    s.RestDaysOfWeek             = restDaysCsv;
                    s.MidCheckWindows            = midCheckWindowsCsv;
                    s.UpdatedAt                  = DateTime.Now;
                }
                SuccessMessage = $"班次「{ShiftName}」已更新";
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        return RedirectToSelf();
    }

    /// <summary>删除班次（停用，保留历史）。</summary>
    public async Task<IActionResult> OnPostDeleteShiftAsync(int id)
    {
        var s = await db.ShiftSchedules.FindAsync(id);
        if (s is not null) { s.IsActive = false; s.UpdatedAt = DateTime.Now; await db.SaveChangesAsync(); }
        return RedirectToSelf();
    }

    /// <summary>批量排班：给勾选的员工（可跨多个组）在日期区间内每天排上指定班次。</summary>
    public async Task<IActionResult> OnPostAssignAsync()
    {
        DateOnly start = default, end = default;
        try
        {
            if (AssignShiftId == 0)
                throw new Exception("请先选择班次");
            if (!DateOnly.TryParse(AssignStart, out start) || !DateOnly.TryParse(AssignEnd, out end))
                throw new Exception("日期格式不正确");
            if (end < start)
                throw new Exception("结束日期不能早于开始日期");
            _ = await db.ShiftSchedules.FindAsync(AssignShiftId) ?? throw new Exception("班次不存在");

            // 勾了“所选组全部成员”就取选中组的全部在职员工；否则用勾选的人（可跨组混排）
            List<int> userIds = AssignAll
                ? await db.Users.Where(u => u.IsActive && u.AttendanceGroupId != null
                                         && CtxGroupIds.Contains(u.AttendanceGroupId.Value))
                                .Select(u => u.Id).ToListAsync()
                : AssignUserIds.Distinct().ToList();

            if (userIds.Count == 0)
                throw new Exception("请至少勾选一名员工，或勾选「所选组全部成员」");

            // 取这些人的入职日期：还没入职的日子不排班
            var hireDates = await db.Users.Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.HireDate);

            // 下面是"每一天 × 每个员工"的双重循环，把这段时间内这些员工已有的排班记录先一次性整批查出来，
            // 按"员工+日期"存进字典。原来的写法是循环里对每个人每一天都单独查一次数据库——
            // 排一个月的班、勾 50 个人，就是 30×50=1500 次数据库查询；现在只查 1 次，结果完全一样，只是快很多。
            var existingAssignments = await db.ShiftAssignments
                .Where(a => userIds.Contains(a.UserId) && a.WorkDate >= start && a.WorkDate <= end)
                .ToDictionaryAsync(a => (a.UserId, a.WorkDate));

            int count = 0, skipped = 0;
            for (var d = start; d <= end; d = d.AddDays(1))
            {
                foreach (var uid in userIds)
                {
                    if (!hireDates.TryGetValue(uid, out var hire) || hire is null || hire.Value > d)
                    {
                        skipped++;
                        continue;
                    }
                    if (existingAssignments.TryGetValue((uid, d), out var existing))
                    {
                        existing.ShiftScheduleId = AssignShiftId;
                    }
                    else
                    {
                        var newAssignment = new ShiftAssignment
                        {
                            UserId          = uid,
                            ShiftScheduleId = AssignShiftId,
                            WorkDate        = d,
                            IsAutoAssigned  = false,
                            CreatedAt       = DateTime.Now
                        };
                        db.ShiftAssignments.Add(newAssignment);
                        existingAssignments[(uid, d)] = newAssignment;   // 存进字典，避免万一循环重复处理同一天同一人时又新建一条
                    }
                    count++;
                }
            }
            await db.SaveChangesAsync();
            if (count == 0)
                throw new Exception("所选员工均未办理入职（入职日期未设置或晚于排班日期），未生成任何排班");

            SuccessMessage = $"排班成功：{count} 条记录"
                + (skipped > 0 ? $"；已跳过 {skipped} 条（员工未入职）" : "");

            // 带上刚排的日期区间，确保新排班在展示窗口内可见
            return RedirectToSelf(start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }

        return RedirectToSelf();
    }

    /// <summary>
    /// 提交后重定向回本页，保持当前选中的考勤组（手动拼 ?groupIds=1&amp;groupIds=2，
    /// 避免 RedirectToPage 对集合路由值处理不一致导致多选丢失）。
    /// </summary>
    /// <summary>
    /// groupIds 不传时用 CtxGroupIds（POST 表单场景，[BindProperty] 会自动绑上）；
    /// GET 处理方法（比如 OnGetExportAsync）里 [BindProperty] 不会绑定，必须显式把当次请求
    /// 实际收到的 groupIds 传进来，不然重定向会把已选的考勤组弄丢，退回到默认只选第一个组。
    /// </summary>
    private IActionResult RedirectToSelf(string? viewStart = null, string? viewEnd = null, IEnumerable<int>? groupIds = null)
    {
        var ids   = (groupIds ?? CtxGroupIds).Where(id => id > 0);
        var parts = ids.Select(id => $"groupIds={id}").ToList();
        if (!string.IsNullOrEmpty(viewStart)) parts.Add("viewStart=" + viewStart);
        if (!string.IsNullOrEmpty(viewEnd))   parts.Add("viewEnd=" + viewEnd);
        var url = "/Admin/ShiftManage" + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");
        return Redirect(url);
    }
}
