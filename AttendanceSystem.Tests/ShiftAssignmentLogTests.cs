using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 排班修改留痕（2026-11-11 用户要求）：谁、什么时候、把谁的哪一天、从什么班次改成什么班次。
/// </summary>
public class ShiftAssignmentLogTests : SqliteTestBase
{
    [Fact]
    public void 服务写入留痕_字段完整()
    {
        using var db = CreateContext();
        var user = U("SAL0", "留痕员工");
        db.Users.Add(user);
        db.SaveChanges();

        var svc = new ShiftAssignmentLogService(db);
        svc.Add(user.Id, new DateOnly(2026, 11, 10), 3, "白班1", 4, "晚班", "修改", "排班管理",
            operatorUserId: 1, operatorName: "测试管理员", remark: "单元测试");
        db.SaveChanges();

        var row = db.ShiftAssignmentLogs.Single();
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(new DateOnly(2026, 11, 10), row.WorkDate);
        Assert.Equal("白班1", row.OldShiftName);
        Assert.Equal("晚班", row.NewShiftName);
        Assert.Equal("修改", row.Action);
        Assert.Equal("排班管理", row.Source);
        Assert.Equal("测试管理员", row.OperatorName);
        Assert.Equal("单元测试", row.Remark);
        Assert.True(row.CreatedAt > new DateTime(2026, 1, 1));
    }

    [Fact]
    public async Task 转班审批回写_会写排班留痕()
    {
        var d = new DateOnly(2026, 11, 10);
        int uid;
        using (var db = CreateContext())
        {
            var group = new AttendanceGroup { GroupName = "留痕转班组" };
            db.AttendanceGroups.Add(group);
            db.SaveChanges();

            var day = DayShift();
            day.AttendanceGroupId = group.Id;
            db.ShiftSchedules.Add(day);
            db.SaveChanges();

            var night = NightShift();
            night.AttendanceGroupId = group.Id;
            db.ShiftSchedules.Add(night);
            db.SaveChanges();

            var user = U("SAL1", "留痕测试");
            user.AttendanceGroupId = group.Id;
            db.Users.Add(user);
            db.SaveChanges();

            db.ShiftAssignments.Add(new ShiftAssignment { UserId = user.Id, WorkDate = d, ShiftScheduleId = day.Id });
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                RequestNo = "SC-LOG-1", ApplicantUserId = user.Id, ApprovalType = ApprovalType.ShiftChange,
                ApprovalStatus = ApprovalStatus.Approved,
                ShiftChangeStartDate = d, ShiftChangeEndDate = d,
                ShiftChangeTargetShiftId = night.Id, ShiftChangeTargetShiftName = "夜班",
                Reason = "单元测试", SubmittedAt = DateTime.Now, UpdatedAt = DateTime.Now
            });
            db.SaveChanges();
            uid = user.Id;
        }

        using (var db = CreateContext())
        {
            var att = new AttendanceService(db, AppOptions, NullLogger<AttendanceService>.Instance);
            var approvalId = db.ApprovalRequests.Single(a => a.RequestNo == "SC-LOG-1").Id;
            await att.UpdateAttendanceAfterApprovalAsync(approvalId);

            var log = db.ShiftAssignmentLogs.Single(l => l.UserId == uid);
            Assert.Equal("修改", log.Action);
            Assert.Equal("转班审批", log.Source);
            Assert.Equal("白班", log.OldShiftName);
            Assert.Equal("夜班", log.NewShiftName);
            Assert.Equal(d, log.WorkDate);
            Assert.Equal("SC-LOG-1", log.Remark);
        }
    }
}
