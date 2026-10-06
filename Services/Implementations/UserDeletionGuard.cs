using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 物理删除员工的"有没有历史数据"检查（网页、AGENT 的提案阶段、执行阶段共用同一份，不各写一份）。
/// 删除用户会按级联外键清空他的考勤记录、打卡流水、月度汇总、申请单和通知，而且不可恢复（只能靠每天 3 点的
/// 数据库备份找回）。所以有任何考勤/打卡/申请记录的人只允许"停用"（停用会从考勤机上删掉、不再被记旷工、
/// 历史数据保留）；只有误建的空账号才允许物理删除（2026-10-06 第 15 轮审查 M14，用户拍板采用这个方案）。
/// </summary>
public static class UserDeletionGuard
{
    public const string BlockedMessage = "该员工已有考勤、打卡或申请记录，不能彻底删除（会一并清空这些历史数据且无法恢复），请改用「停用」";

    /// <summary>这个人名下有没有考勤记录、打卡流水或自己提交的申请单。</summary>
    public static async Task<bool> HasHistoryDataAsync(AttendanceDbContext db, int userId, CancellationToken ct = default) =>
        await db.AttendanceRecords.AnyAsync(r => r.UserId == userId, ct)
        || await db.AttendancePunches.AnyAsync(p => p.UserId == userId, ct)
        || await db.ApprovalRequests.AnyAsync(a => a.ApplicantUserId == userId, ct);
}
