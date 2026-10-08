using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>只读工具：查员工、登记列表、考勤异常、月度汇总、设备、部门、考勤组（<see cref="AgentToolExecutor"/> 的一部分）。</summary>
public partial class AgentToolExecutor
{
    // ── 工具 1：查员工 ───────────────────────────────────────────────────────

    private async Task<string> UserSearchAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var keyword = StrArg(args, "keyword")?.Trim();
        var status  = StrArg(args, "status");
        var limit   = Math.Clamp(IntArg(args, "limit") ?? 20, 1, 50);

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var subtree = await ResolveSubtreeAsync(visibleIds, IntArg(args, "deptId"), ct);
        if (subtree is { Count: 0 }) return "查询结果为空（请求的部门不在你的管理范围内）";

        var q = db.Users.AsNoTracking();
        if (!string.IsNullOrEmpty(keyword))
            q = q.Where(u => u.EmployeeNo.Contains(keyword) || u.RealName.Contains(keyword));
        if (visibleIds is not null)
            q = q.Where(u => u.DepartmentId != null && visibleIds.Contains(u.DepartmentId.Value));
        else if (subtree is { Count: > 0 })
            q = q.Where(u => u.DepartmentId != null && subtree.Contains(u.DepartmentId.Value));

        q = status switch
        {
            "active"      => q.Where(u => u.IsActive && !u.IsBlacklisted),
            "disabled"    => q.Where(u => !u.IsActive && !u.IsBlacklisted),
            "blacklisted" => q.Where(u => u.IsBlacklisted),
            _             => q
        };

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(u => u.EmployeeNo).Take(limit)
            .Select(u => new
            {
                u.Id, u.EmployeeNo, u.RealName, u.Role, u.IsActive, u.IsBlacklisted, u.Phone,
                DeptName = u.Department != null ? u.Department.DeptName : null,
                GroupName = u.AttendanceGroup != null ? u.AttendanceGroup.GroupName : null
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return total == 0 ? "未找到匹配的员工" : "（无更多结果）";

        var sb = new System.Text.StringBuilder();
        sb.Append($"共匹配 {total} 人，列出前 {rows.Count} 条（行首括号内数字是 userId，写操作工具需要用它）：\n");
        foreach (var r in rows)
            sb.AppendLine($"(id:{r.Id}) {r.EmployeeNo} | {r.RealName} | 部门:{(r.DeptName ?? "未分配")} | 组:{(r.GroupName ?? "-")} | {RoleText(r.Role)} | {(r.IsBlacklisted ? "黑名单" : r.IsActive ? "在职" : "停用")} | 手机:{MaskPhone(r.Phone)}");
        sb.Append("提示：如需某人的更多资料，请告知其工号，在员工档案页查看（助手不返回身份证/住址等敏感信息）。");
        return Truncate(sb.ToString(), 6000);
    }

    // ── 工具 2：待确认登记列表 ───────────────────────────────────────────────

    private async Task<string> PendingRegistrationListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var limit = Math.Clamp(IntArg(doc.RootElement, "limit") ?? 30, 1, 50);

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.EmployeeRegistrations.AsNoTracking()
            .Where(r => r.Status == RegistrationStatus.Pending);
        if (visibleIds is not null)
            q = q.Where(r => r.DepartmentId != null && visibleIds.Contains(r.DepartmentId.Value));

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.SubmittedAt).Take(limit)
            .Select(r => new { r.Id, r.RealName, r.Phone, r.SubmittedAt, DeptName = r.Department != null ? r.Department.DeptName : null })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return total == 0
                ? "当前没有待确认的登记。"
                : "（无更多结果）";

        var sb = new System.Text.StringBuilder();
        sb.Append($"共 {total} 条待确认登记，列出前 {rows.Count} 条（已脱敏）：\n");
        foreach (var r in rows)
            sb.AppendLine($"#{r.Id} {r.RealName} | 手机:{MaskPhone(r.Phone)} | 意向部门:{(r.DeptName ?? "未指定(由总部处理)")} | 提交:{r.SubmittedAt:yyyy-MM-dd HH:mm}");
        sb.Append("提示：如需驳回某条登记，可继续调用 registration_reject_propose 生成待确认动作，由管理员确认后执行。");
        return Truncate(sb.ToString(), 4000);
    }

    // ── 工具 3：考勤异常清单 ──────────────────────────────────────────────────

    private const int MaxAnomalyQueryDays = 92;

    private async Task<string> AttendanceAnomalyListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;

        var startText = StrArg(args, "start");
        if (!DateOnly.TryParse(startText, out var start))
            return "错误：start 参数格式应为 yyyy-MM-dd（如 2026-09-01）";
        var end = DateOnly.TryParse(StrArg(args, "end"), out var e) ? e : start;
        if (end < start) (start, end) = (end, start);
        // 不限跨度的话，一句"查 2000-01-01 到 2100-01-01 的全部异常"就会对 22 万行且还在增长的 AttendanceRecord
        // 做全表 count + join（2026-10-06 复核发现），超了让模型按月/按季分段查
        if (end.DayNumber - start.DayNumber + 1 > MaxAnomalyQueryDays)
            return $"错误：单次查询的日期跨度不能超过 {MaxAnomalyQueryDays} 天，请缩小范围或分段查询";
        var type  = StrArg(args, "type");
        var limit = Math.Clamp(IntArg(args, "limit") ?? 50, 1, 100);

        List<AttendanceStatus> statuses = type switch
        {
            "late"       => [AttendanceStatus.Late],
            "early"      => [AttendanceStatus.EarlyLeave],
            "absent"     => [AttendanceStatus.Absent],
            "notpunched" => [AttendanceStatus.NotPunched],
            _            => [AttendanceStatus.Late, AttendanceStatus.EarlyLeave, AttendanceStatus.Absent, AttendanceStatus.NotPunched]
        };

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var subtree = await ResolveSubtreeAsync(visibleIds, IntArg(args, "deptId"), ct);
        if (subtree is { Count: 0 }) return "查询结果为空（请求的部门不在你的管理范围内）";

        var q = db.AttendanceRecords.AsNoTracking()
            .Where(r => r.WorkDate >= start && r.WorkDate <= end && statuses.Contains(r.AttendanceStatus)
                        && r.User.Role == UserRole.Employee && !r.User.IsAttendanceExempt);   // 免考勤的正式工不进异常清单
        if (visibleIds is not null)
            q = q.Where(r => r.User.DepartmentId != null && visibleIds.Contains(r.User.DepartmentId.Value));
        else if (subtree is not null)
            q = q.Where(r => r.User.DepartmentId != null && subtree.Contains(r.User.DepartmentId.Value));

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.WorkDate).ThenBy(r => r.User.RealName).Take(limit)
            .Select(r => new
            {
                r.WorkDate, r.AttendanceStatus, r.LateMinutes, r.EarlyLeaveMinutes,
                r.ClockInTime, r.ClockOutTime,
                Eno = r.User.EmployeeNo, Name = r.User.RealName
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return $"在 {start:yyyy-MM-dd} 至 {end:yyyy-MM-dd} 内未找到考勤异常记录。";

        var sb = new System.Text.StringBuilder();
        sb.Append($"{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}，异常共 {total} 条，列出前 {rows.Count} 条：\n");
        foreach (var r in rows)
        {
            var detail = new System.Collections.Generic.List<string> { AttStatusText(r.AttendanceStatus) };
            // 走全系统统一口径（只认状态本身就是迟到/早退的记录）：状态已被后台改成旷工/未打卡的记录，
            // 字段里可能还残留几百分钟，直接拼进回复会跟报表里的 0 自相矛盾（2026-10-06 复核发现）
            var lateMin  = AttendanceService.EffectiveLateMinutes(r.AttendanceStatus, r.LateMinutes);
            var earlyMin = AttendanceService.EffectiveEarlyLeaveMinutes(r.AttendanceStatus, r.EarlyLeaveMinutes);
            if (lateMin > 0) detail.Add($"迟到{lateMin}分");
            if (earlyMin > 0) detail.Add($"早退{earlyMin}分");
            var ci = r.ClockInTime?.ToString("HH:mm") ?? "-";
            var co = r.ClockOutTime?.ToString("HH:mm") ?? "-";
            sb.AppendLine($"{r.WorkDate:MM-dd} {r.Name}({r.Eno}) [{string.Join("，", detail)}] 上班{ci} 下班{co}");
        }
        return Truncate(sb.ToString(), 6000);
    }

    // ── 工具 4：月度汇总 ──────────────────────────────────────────────────────

    private async Task<string> MonthlySummaryGetAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var year  = IntArg(args, "year");
        var month = IntArg(args, "month");
        var limit = Math.Clamp(IntArg(args, "limit") ?? 20, 1, 50);
        if (!year.HasValue || !month.HasValue || month is < 1 or > 12)
            return "错误：year(如2026) 与 month(1-12) 参数必填且合法";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.MonthlyAttendanceSummaries.AsNoTracking()
            .Where(s => s.Year == year.Value && s.Month == month.Value
                        && s.User.Role == UserRole.Employee && !s.User.IsAttendanceExempt);   // 免考勤的正式工不进汇总统计
        if (visibleIds is not null)
            q = q.Where(s => s.User.DepartmentId != null && visibleIds.Contains(s.User.DepartmentId.Value));

        var exists = await q.AnyAsync(ct);
        if (!exists)
            return $"{year}年{month}月还没有生成汇总数据（系统通常在月初自动生成上个月；或管理员先在\"月度报表\"页生成）。助手不会为了查询而触发全公司重算。";

        var totals = await q.GroupBy(_ => 1)
            .Select(g => new
            {
                People   = g.Count(),
                Late     = g.Sum(x => x.LateCount),
                Early    = g.Sum(x => x.EarlyLeaveCount),
                Absent   = g.Sum(x => x.AbsentDays),
                NotPunch = g.Sum(x => x.NotPunchedCount),
                Leave    = g.Sum(x => x.LeaveDays),
                WorkH    = g.Sum(x => x.TotalWorkHours),
                OverH    = g.Sum(x => x.TotalOvertimeHours)
            })
            .FirstOrDefaultAsync(ct);

        var rows = await q.OrderByDescending(s => s.LateCount + s.EarlyLeaveCount + s.AbsentDays)
            .Take(limit)
            .Select(s => new { s.LateCount, s.EarlyLeaveCount, s.AbsentDays, s.NotPunchedCount, s.LeaveDays, s.TotalWorkHours, Eno = s.User.EmployeeNo, Name = s.User.RealName })
            .ToListAsync(ct);

        var sb = new System.Text.StringBuilder();
        if (totals is not null)
            sb.AppendLine($"{year}年{month}月 汇总：覆盖 {totals.People} 人 | 迟到{totals.Late}次 早退{totals.Early}次 旷工{totals.Absent}天 缺卡{totals.NotPunch}次 请假{totals.Leave}天 总工时{totals.WorkH:0.#}h 加班{totals.OverH:0.#}h");
        sb.AppendLine($"异常最多的 {rows.Count} 人：");
        foreach (var r in rows)
            sb.AppendLine($"{r.Name}({r.Eno}) 迟到{r.LateCount} 早退{r.EarlyLeaveCount} 旷工{r.AbsentDays}天 缺卡{r.NotPunchedCount} 请假{r.LeaveDays}天 工时{r.TotalWorkHours:0.#}h");
        var first = new DateOnly(year.Value, month.Value, 1);
        var last = first.AddMonths(1).AddDays(-1);
        sb.AppendLine($"如需导出 Excel，请告知管理员打开：/Report/MonthlyReport?start={first:yyyy-MM-dd}&end={last:yyyy-MM-dd}（该页面右上角有导出按钮，助手本身不生成文件）。");
        return Truncate(sb.ToString(), 5000);
    }

    // ── 工具 5：设备状态 ──────────────────────────────────────────────────────

    private async Task<string> DeviceStatusListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var onlineOnly = BoolArg(args, "onlineOnly") ?? false;
        var limit = Math.Clamp(IntArg(args, "limit") ?? 50, 1, 100);

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.ZKDevices.AsNoTracking();
        if (visibleIds is not null)
            q = q.Where(d => d.DepartmentId != null && visibleIds.Contains(d.DepartmentId.Value));

        var threshold = clock.LocalNow().AddMinutes(-5);
        if (onlineOnly)
            q = q.Where(d => d.LastSeenAt != null && d.LastSeenAt >= threshold);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(d => d.Name).ThenBy(d => d.SN).Take(limit)
            .Select(d => new { d.Id, d.SN, d.Name, d.IsActive, d.LastSeenAt, DeptName = d.Department != null ? d.Department.DeptName : null })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return total == 0 ? "范围内没有考勤机。" : "（无更多结果）";

        var sb = new System.Text.StringBuilder();
        sb.Append($"范围内考勤机共 {total} 台，列出前 {rows.Count} 台：\n");
        foreach (var d in rows)
        {
            var state = !d.LastSeenAt.HasValue ? "从未连接"
                      : d.LastSeenAt.Value >= threshold ? "在线" : "离线";
            var last = d.LastSeenAt.HasValue ? $"最近通信 {d.LastSeenAt:MM-dd HH:mm}" : "";
            sb.AppendLine($"(id:{d.Id}) {(d.Name ?? d.SN)} | SN:{d.SN} | 部门:{(d.DeptName ?? "总部/未归类")} | {(d.IsActive ? "启用" : "停用")} | {state} {last}");
        }
        return Truncate(sb.ToString(), 6000);
    }

    // ── 工具 6：部门树 ────────────────────────────────────────────────────────

    private async Task<string> DepartmentListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var keyword = StrArg(doc.RootElement, "keyword")?.Trim();

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.Departments.AsNoTracking().Where(d => d.IsActive);
        if (visibleIds is not null)
            q = q.Where(d => visibleIds.Contains(d.Id));
        if (!string.IsNullOrEmpty(keyword))
            q = q.Where(d => d.DeptName.Contains(keyword));

        var rows = await q.OrderBy(d => d.SortIndex).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.DeptName, d.ParentId, d.CompanyName, d.AttendanceGroupId,
                                ParentName = d.ParentDepartment != null ? d.ParentDepartment.DeptName : null })
            .Take(200)
            .ToListAsync(ct);

        if (rows.Count == 0) return "范围内没有匹配的部门。";

        var sb = new System.Text.StringBuilder();
        sb.Append($"范围内部门共 {rows.Count} 个（行首括号内数字是 deptId）：\n");
        foreach (var d in rows)
            sb.AppendLine($"(id:{d.Id}) {d.DeptName} | 上级:{(d.ParentName ?? "无(顶级)")} | 公司:{(d.CompanyName ?? "-")} | 绑定考勤组:{(d.AttendanceGroupId?.ToString() ?? "未绑定")}");
        return Truncate(sb.ToString(), 6000);
    }

    // ── 工具 7：考勤组列表 ────────────────────────────────────────────────────

    private async Task<string> AttendanceGroupListAsync(int operatorUserId, CancellationToken ct)
    {
        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.AttendanceGroups.AsNoTracking();
        if (visibleIds is not null)
            q = q.Where(g => db.Departments.Any(d => d.AttendanceGroupId == g.Id && visibleIds.Contains(d.Id)));

        var rows = await q.OrderBy(g => g.Id)
            .Select(g => new { g.Id, g.GroupName, g.ApprovalLevel, g.EnableLocationPunch })
            .Take(100)
            .ToListAsync(ct);

        if (rows.Count == 0) return "范围内没有考勤组。";

        var sb = new System.Text.StringBuilder();
        sb.Append($"范围内考勤组共 {rows.Count} 个（行首括号内数字是 groupId）：\n");
        foreach (var g in rows)
            sb.AppendLine($"(id:{g.Id}) {g.GroupName} | 审批层级:{(g.ApprovalLevel == ApprovalLevelType.Level2 ? "二级(班组长+主管)" : "一级(班组长)")} | 定位打卡:{(g.EnableLocationPunch ? "开启" : "关闭")}");
        return Truncate(sb.ToString(), 6000);
    }
}
