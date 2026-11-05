using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// ─────────────────────────────────────────────────────────────────────────────
// 一次性维护工具：核减某人的一张已批准事假单 + 按实际出勤重算后续日子的工时 + 刷新月度汇总。
//
// 典型用途（2026-11-05 曹宗保）：假单写的是 10-21~11-22，但人从 10-24 起就照常上班打卡了，
// 结果是"请假 8 小时 + 正班 0"，实际出勤被漏算。做法：把假单截止到真正没来的那一天，
// 之后的记录恢复成按打卡结算，再调用系统自己的重算/汇总逻辑，避免手写 SQL 把工时算错。
//
// 用法（在服务器上、应用目录下执行）：
//   dotnet AttMaint.dll --appsettings /var/www/attendance-net/appsettings.json \
//        --user-no 001360 --leave-no QJ202610200006 --leave-end 2026-10-23T17:30:00 \
//        --record-end 2026-11-22 --dry-run
// 去掉 --dry-run 才会真正改库（改之前会自动建备份表）。
// ─────────────────────────────────────────────────────────────────────────────

string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var appsettings = Arg("--appsettings") ?? "appsettings.json";
var userNo      = Arg("--user-no") ?? throw new ArgumentException("缺少 --user-no");
var leaveNo     = Arg("--leave-no") ?? throw new ArgumentException("缺少 --leave-no");
var leaveEndArg = Arg("--leave-end") ?? throw new ArgumentException("缺少 --leave-end（例：2026-10-23T17:30:00）");
var recordEndArg= Arg("--record-end") ?? throw new ArgumentException("缺少 --record-end（例：2026-11-22）");
var dryRun      = args.Contains("--dry-run");
var newLeaveEnd = DateTime.Parse(leaveEndArg);
var recordEnd   = DateOnly.Parse(recordEndArg);

var cfg = new ConfigurationBuilder()
    .SetBasePath(Path.GetDirectoryName(Path.GetFullPath(appsettings))!)
    .AddJsonFile(Path.GetFileName(appsettings), optional: false)
    .Build();
var connStr = cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("appsettings.json 里没有 Default 连接串");

var builder = new DbContextOptionsBuilder<AttendanceDbContext>().UseMySql(connStr, ServerVersion.AutoDetect(connStr));
await using var db = new AttendanceDbContext(builder.Options);

var appSettings = cfg.GetSection(AppSettingsOptions.SectionName).Get<AppSettingsOptions>() ?? new AppSettingsOptions();
var attendance = new AttendanceService(db, Options.Create(appSettings), NullLogger<AttendanceService>.Instance);

Console.WriteLine($"{(dryRun ? "[预演] " : "")}员工工号：{userNo}，假单：{leaveNo}，新的假单截止：{newLeaveEnd:yyyy-MM-dd HH:mm}，记录处理到：{recordEnd:yyyy-MM-dd}");

// ── 1. 人员 & 假单 ──────────────────────────────────────────────────────────
var user = await db.Users.FirstOrDefaultAsync(u => u.EmployeeNo == userNo)
           ?? throw new InvalidOperationException($"找不到工号 {userNo}");
var approval = await db.ApprovalRequests.FirstOrDefaultAsync(a => a.RequestNo == leaveNo)
               ?? throw new InvalidOperationException($"找不到申请单 {leaveNo}");
if (approval.ApprovalType != ApprovalType.Leave || approval.ApprovalStatus != ApprovalStatus.Approved)
    throw new InvalidOperationException($"申请单 {leaveNo} 不是已批准的请假单");
if (approval.LeaveStartTime is not { } oldStart || approval.LeaveEndTime is not { } oldEnd)
    throw new InvalidOperationException($"申请单 {leaveNo} 没有请假起止时间");

var startDay = DateOnly.FromDateTime(oldStart);
var newEndDay = DateOnly.FromDateTime(newLeaveEnd);
if (newEndDay < startDay) throw new InvalidOperationException("新截止日期早于请假开始日期");

// 新截止后重新算请假时长（按每天和班次上班时段的交集，跟回写/审核同一口径）
var shifts = (await db.ShiftAssignments.Include(a => a.ShiftSchedule)
        .Where(a => a.UserId == user.Id && a.WorkDate >= startDay && a.WorkDate <= newEndDay)
        .ToListAsync())
    .ToDictionary(a => a.WorkDate, a => a.ShiftSchedule);
var dailyCap = appSettings.DefaultDailyWorkHours;
decimal newDuration = 0;
for (var d = startDay; d <= newEndDay; d = d.AddDays(1))
{
    shifts.TryGetValue(d, out var shift);
    newDuration += AttendanceService.ComputeLeaveHoursForDay(d, oldStart, newLeaveEnd, shift?.StandardWorkHours ?? dailyCap, shift);
}
Console.WriteLine($"假单原区间：{oldStart:yyyy-MM-dd HH:mm} ~ {oldEnd:yyyy-MM-dd HH:mm}（{approval.LeaveDurationHours:0.##} 小时）");
Console.WriteLine($"假单新区间：{oldStart:yyyy-MM-dd HH:mm} ~ {newLeaveEnd:yyyy-MM-dd HH:mm}（{newDuration:0.##} 小时）");

// ── 2. 受影响的考勤记录 ─────────────────────────────────────────────────────
var fromDay = newEndDay.AddDays(1);
var records = await db.AttendanceRecords
    .Where(r => r.UserId == user.Id && r.WorkDate >= fromDay && r.WorkDate <= recordEnd)
    .OrderBy(r => r.WorkDate)
    .ToListAsync();
Console.WriteLine($"待处理考勤记录（{fromDay:yyyy-MM-dd} ~ {recordEnd:yyyy-MM-dd}）：{records.Count} 条");

var today = DateOnly.FromDateTime(DateTime.Now);
var toDelete = records.Where(r => r.ClockInTime is null && r.ClockOutTime is null && r.WorkDate > today).ToList();
var toReset  = records.Where(r => !toDelete.Contains(r)).ToList();
foreach (var r in toDelete)
    Console.WriteLine($"  删除（纯请假占位、没有任何打卡）：{r.WorkDate} 状态={(int)r.AttendanceStatus}");
foreach (var r in toReset)
    Console.WriteLine($"  重置：{r.WorkDate} 状态={(int)r.AttendanceStatus} 正班={r.ActualWorkHours:0.##} 加班={r.OvertimeHours:0.##}"
                      + (r.ClockInTime is { } ci ? $" 上班={ci:HH:mm}" : " 无上班卡")
                      + (r.ClockOutTime is { } co ? $" 下班={co:HH:mm}" : " 无下班卡"));

if (dryRun)
{
    Console.WriteLine("[预演] 未做任何修改。");
    return;
}

// ── 3. 备份 → 改假单 → 重置记录 ────────────────────────────────────────────
var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
var bakRec = $"bak_AttMaint_rec_{stamp}";
var bakApp = $"bak_AttMaint_app_{stamp}";
var bakSum = $"bak_AttMaint_sum_{stamp}";
var recRange = $"UserId = {user.Id} AND WorkDate >= '{fromDay:yyyy-MM-dd}' AND WorkDate <= '{recordEnd:yyyy-MM-dd}'";
await db.Database.ExecuteSqlRawAsync($"CREATE TABLE {bakRec} AS SELECT * FROM AttendanceRecord WHERE {recRange}");
await db.Database.ExecuteSqlRawAsync($"CREATE TABLE {bakApp} AS SELECT * FROM ApprovalRequest WHERE Id = {approval.Id}");
await db.Database.ExecuteSqlRawAsync(
    $"CREATE TABLE {bakSum} AS SELECT * FROM MonthlyAttendanceSummary WHERE UserId = {user.Id} AND Year = 2026");
Console.WriteLine($"备份表：{bakRec} / {bakApp} / {bakSum}");

approval.LeaveEndTime = newLeaveEnd;
approval.LeaveDurationHours = newDuration;
approval.Reason = ((approval.Reason ?? "") + "；2026-11-05 按实际出勤核减为 "
                 + $"{oldStart:MM-dd}~{newLeaveEnd:MM-dd}（其余日期取消）").Trim('；');

foreach (var r in toDelete) db.AttendanceRecords.Remove(r);
foreach (var r in toReset)
{
    r.AttendanceStatus = AttendanceStatus.Normal;
    r.LeaveHours = 0;
    r.LateMinutes = 0;
    r.EarlyLeaveMinutes = 0;
    r.ActualWorkHours = 0;
    r.ApprovalNote = ((r.ApprovalNote ?? "") + $"；假单已核减为 {oldStart:MM-dd}~{newLeaveEnd:MM-dd}，这天按实际出勤重算").Trim('；');
    r.UpdatedAt = DateTime.Now;
}
await db.SaveChangesAsync();
Console.WriteLine("假单与考勤记录已更新，开始按系统规则重算工时……");

// ── 4. 用系统自己的规则重算工时（会顺带刷新月度汇总）────────────────────────
var recalc = await attendance.RecalcHoursForScheduledAsync([user.Id], fromDay, today, dryRun: false);
Console.WriteLine($"重算生效：{recalc.Count} 条");

// 保险起见再显式生成 10、11 月的汇总（该人）
await attendance.GenerateMonthlySummaryAsync(2026, 10, [user.Id]);
await attendance.GenerateMonthlySummaryAsync(2026, 11, [user.Id]);
Console.WriteLine("月度汇总已重算（2026-10、2026-11）。");

// ── 5. 结果打印 ─────────────────────────────────────────────────────────────
db.ChangeTracker.Clear();
var final = await db.AttendanceRecords
    .Where(r => r.UserId == user.Id && r.WorkDate >= startDay && r.WorkDate <= recordEnd)
    .OrderBy(r => r.WorkDate).ToListAsync();
Console.WriteLine("\n最终记录：");
foreach (var r in final)
    Console.WriteLine($"  {r.WorkDate} 状态={(int)r.AttendanceStatus,-2} 请假={r.LeaveHours:0.##}h 正班={r.ActualWorkHours:0.##}h 加班={r.OvertimeHours:0.##}h");

var sums = await db.MonthlyAttendanceSummaries
    .Where(s => s.UserId == user.Id && s.Year == 2026 && (s.Month == 10 || s.Month == 11))
    .OrderBy(s => s.Month).ToListAsync();
Console.WriteLine("\n月度汇总：");
foreach (var s in sums)
    Console.WriteLine($"  {s.Year}-{s.Month:00} 应出勤={s.ExpectedWorkdays} 实际出勤={s.ActualWorkdays} " +
                      $"正班={s.TotalWorkHours:0.##} 加班={s.TotalOvertimeHours:0.##} 请假天={s.LeaveDays} 迟到={s.LateCount} 早退={s.EarlyLeaveCount} 旷工={s.AbsentDays} 缺卡={s.NotPunchedCount}");
