using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 管理员手动补卡的操作记录（对应数据库表 PunchAdjustLog）：每补一次卡存一条，连同补卡前那天的原样
/// （上下班时间、状态、工时、迟到早退、午间命中），"撤回"按钮就靠它把这天恢复成补卡前的样子。
/// 只记录这次功能上线之后的手动补卡；更早的补卡没有"补卡前的样子"可恢复。
/// </summary>
[Table("PunchAdjustLog")]
public class PunchAdjustLog
{
    [Key] public int Id { get; set; }

    public int      UserId   { get; set; }                  // 给谁补的卡
    public DateOnly WorkDate { get; set; }                  // 补的是哪个考勤日

    [MaxLength(50)]
    public string? OperatorName { get; set; }              // 谁操作的

    /// <summary>补的是哪种卡：上班 / 下班 / 午间 / 上班+下班（旧版两个时间一起填）。</summary>
    [Required, MaxLength(20)]
    public string AdjustType { get; set; } = "";

    [MaxLength(200)]
    public string? Remark { get; set; }

    // ── 补卡前这天考勤记录的样子（撤回时恢复这些）──
    public bool      RecordExisted      { get; set; }       // 补卡前这天有没有考勤记录；没有的话撤回时会把补卡建出来的记录一起删掉
    public DateTime? PrevClockIn        { get; set; }
    public DateTime? PrevClockOut       { get; set; }
    public AttendanceStatus PrevStatus  { get; set; }
    public decimal   PrevActualWorkHours { get; set; }
    public int       PrevLateMinutes    { get; set; }
    public int       PrevEarlyLeaveMinutes { get; set; }

    [MaxLength(500)]
    public string? PrevMidCheckResults  { get; set; }

    // ── 补卡后这天的上下班时间（撤回前核对：如果之后又被别的操作改过，就不能直接撤回）──
    public DateTime? NewClockIn         { get; set; }
    public DateTime? NewClockOut        { get; set; }

    /// <summary>补的是午间卡时，新增的那条午间打卡的时间（撤回时把它删掉）；其余为空。</summary>
    public DateTime? AddedMidCheckTime  { get; set; }

    public DateTime  CreatedAt          { get; set; } = DateTime.Now;

    // ── 撤回信息 ──
    public DateTime? RevertedAt         { get; set; }
    [MaxLength(50)]
    public string?   RevertedBy         { get; set; }
}
