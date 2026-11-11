using AttendanceSystem.Data;
using AttendanceSystem.Models.Entities;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 排班修改留痕：调 <see cref="Add"/> 把一条变更放进 DbContext，随调用方的 SaveChanges 一起落库
/// （2026-11-11 用户要求：排班被覆盖后能查到是谁改的、从什么班次改成什么班次）。
/// </summary>
public class ShiftAssignmentLogService(AttendanceDbContext db)
{
    public void Add(
        int userId, DateOnly workDate,
        int? oldShiftId, string? oldShiftName,
        int? newShiftId, string? newShiftName,
        string action, string source,
        int? operatorUserId = null, string? operatorName = null, string? remark = null)
    {
        db.ShiftAssignmentLogs.Add(new ShiftAssignmentLog
        {
            UserId              = userId,
            WorkDate            = workDate,
            OldShiftScheduleId  = oldShiftId,
            OldShiftName        = oldShiftName,
            NewShiftScheduleId  = newShiftId,
            NewShiftName        = newShiftName,
            Action              = action,
            Source              = source,
            OperatorUserId      = operatorUserId,
            OperatorName        = operatorName,
            Remark              = remark,
            CreatedAt           = DateTime.Now
        });
    }
}
