using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 员工考勤异议单（考勤核对功能，2026-11-03 新增）：
/// 员工在"考勤核对"页看到近 7 天的考勤记录，哪一天觉得不对就提一张异议，管理员/文员收到通知后处理。
/// 要点：① 这张单**不改考勤数据**，只是工单；管理员仍用"手动补卡/手动调整"改数据，改完在这张单上写处理结果。
/// ② 员工不操作 = 认可系统记录，不产生任何数据。
/// ③ 提交时把当时的记录快照存下来（<see cref="Snapshot"/>），事后记录被改也能看出"当时是什么样"。
/// </summary>
public class AttendanceDispute
{
    public int      Id         { get; set; }

    /// <summary>提异议的员工。</summary>
    public int      UserId     { get; set; }

    /// <summary>异议的考勤日（不是提交日）。</summary>
    public DateOnly WorkDate   { get; set; }

    /// <summary>提交时对应的考勤记录（那天可能还没有记录，所以可空）。</summary>
    public int?     RecordId   { get; set; }

    /// <summary>提交时的记录快照文字（上班卡/下班卡/工时/状态），事后记录被改也看得到。</summary>
    public string?  Snapshot   { get; set; }

    public AttendanceDisputeType Type { get; set; }

    /// <summary>员工说明的原因（必填）。</summary>
    public string   Reason     { get; set; } = "";

    /// <summary>员工期望的结果（可选，比如"我 18:20 才下班"）。</summary>
    public string?  ExpectedText { get; set; }

    public AttendanceDisputeStatus Status { get; set; } = AttendanceDisputeStatus.Pending;

    /// <summary>处理人（管理员/文员）。</summary>
    public int?     HandlerUserId { get; set; }
    public string?  HandlerName   { get; set; }
    public DateTime? HandledAt    { get; set; }

    /// <summary>处理回复（员工可见）。</summary>
    public string?  Reply      { get; set; }

    public DateTime CreatedAt  { get; set; } = DateTime.Now;
    public DateTime UpdatedAt  { get; set; } = DateTime.Now;
}
