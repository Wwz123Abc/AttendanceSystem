using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 排班修改留痕（对应数据库表 ShiftAssignmentLog）：每次排班被新增/修改/删除都写一条，
/// 记录"谁、什么时候、把谁的哪一天、从什么班次改成什么班次"。
/// 2026-11-11 用户要求：之前排班被批量覆盖后查不到是谁改的，只能靠 nginx 日志反推 IP。
/// </summary>
[Table("ShiftAssignmentLog")]
public class ShiftAssignmentLog
{
    [Key] public int Id { get; set; }

    public int      UserId   { get; set; }                  // 被排班的人
    public DateOnly WorkDate { get; set; }                  // 哪一天的排班

    public int?      OldShiftScheduleId { get; set; }       // 改之前的班次（新增时为 null）
    [MaxLength(50)] public string? OldShiftName { get; set; }

    public int?      NewShiftScheduleId { get; set; }       // 改之后的班次（删除时为 null）
    [MaxLength(50)] public string? NewShiftName { get; set; }

    /// <summary>变更类型：新增 / 修改 / 删除。</summary>
    [Required, MaxLength(10)]
    public string Action { get; set; } = "";

    /// <summary>从哪里改的：排班管理 / 新建员工 / 转班审批 / 其他。</summary>
    [Required, MaxLength(20)]
    public string Source { get; set; } = "";

    public int?      OperatorUserId { get; set; }           // 操作人账号（系统自动操作时为空）
    [MaxLength(50)]  public string? OperatorName { get; set; }

    [MaxLength(200)] public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
