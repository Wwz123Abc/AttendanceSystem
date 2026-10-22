namespace AttendanceSystem.Models.Enums;

/// <summary>审批状态：一张申请单当前走到哪一步。</summary>
public enum ApprovalStatus
{
    Pending    = 1,  // 待审批：刚提交，等第一个人审
    InProgress = 2,  // 审批中：多级审批里，前面通过了、还没走完
    Approved   = 3,  // 已通过：全部审批人都同意
    Rejected   = 4,  // 已驳回：被某个审批人否决
    Cancelled  = 5,  // 已撤销：申请人自己撤回了
    Expired    = 6   // 已作废：超过有效期（15 天）还没人处理，系统自动作废（只对 2026-10-23 起提交的申请生效）
}
