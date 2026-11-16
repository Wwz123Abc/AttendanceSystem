using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 补卡把"迟到满 4 小时"改小之后，工时必须恢复满额、旧半天备注要清掉（2026-11-16 修复）。
/// 真实案例：雷得莹/葛庆腾 11-09、张浩琳/于卓民 11-11——当天漏打上班卡被按半天旷工封顶 4 小时，
/// 补卡通过后上班时间改早、迟到分钟变 0，但工时仍停在 4 小时、备注还写着"按半天旷工计"。
/// </summary>
public class PunchReplenishmentHalfDayTests : SqliteTestBase
{
    private static readonly DateOnly RuleDate = new(2026, 11, 9);   // 半天旷工规则生效日

    private (int userId, int shiftId) SeedLateRecord()
    {
        using var db = CreateContext();
        var group = new AttendanceGroup { GroupName = "补卡半天测试组" };
        db.AttendanceGroups.Add(group);
        db.SaveChanges();

        var shift = DayShift();                 // 08:30–17:30，标准 8 小时
        shift.AttendanceGroupId = group.Id;
        db.ShiftSchedules.Add(shift);
        db.SaveChanges();

        var user = U("HDA9", "补卡半天测试");
        user.AttendanceGroupId = group.Id;
        db.Users.Add(user);
        db.SaveChanges();

        db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = RuleDate, ShiftScheduleId = shift.Id });
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            UserId = user.Id, WorkDate = RuleDate,
            ClockInTime  = RuleDate.ToDateTime(new TimeOnly(13, 0)),   // 漏打上班卡，13:00 才打卡 → 迟到 270 分钟
            ClockOutTime = RuleDate.ToDateTime(new TimeOnly(17, 30)),
            AttendanceStatus = AttendanceStatus.Late,
            LateMinutes = 270,
            ActualWorkHours = 4,                                        // 被"迟到满 4 小时"封顶成半天
            ApprovalNote = "按半天旷工计（迟到 270 分钟），工时按半天"
        });
        db.SaveChanges();
        return (user.Id, shift.Id);
    }

    private static string NewNo(string prefix) => prefix + "-HDA-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task 补卡把迟到改小后_工时恢复满额_并清掉半天备注()
    {
        var (uid, _) = SeedLateRecord();

        var requestId = await AddApprovedAsync(new ApprovalRequest
        {
            RequestNo = NewNo("BK"), ApplicantUserId = uid,
            ApprovalType = ApprovalType.PunchReplenishment,
            PunchDate = RuleDate, PunchType = PunchType.ClockIn,
            PunchTime = new TimeOnly(8, 30), Reason = "补上班卡"
        });

        using var db = CreateContext();
        var svc = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
        await svc.UpdateAttendanceAfterApprovalAsync(requestId);

        db.ChangeTracker.Clear();
        var rec = await db.AttendanceRecords.SingleAsync(r => r.UserId == uid && r.WorkDate == RuleDate);

        Assert.Equal(RuleDate.ToDateTime(new TimeOnly(8, 30)), rec.ClockInTime);   // 补卡写入生效
        Assert.Equal(8m, rec.ActualWorkHours);            // 修正前会是 4（沿用旧迟到分钟封顶）
        Assert.Equal(0, rec.LateMinutes);
        Assert.Equal(AttendanceStatus.Normal, rec.AttendanceStatus);
        Assert.DoesNotContain(AttendanceService.HalfDayAbsentNoteMarker, rec.ApprovalNote ?? "");
        Assert.Contains("补卡已审批通过", rec.ApprovalNote ?? "");
    }
}
