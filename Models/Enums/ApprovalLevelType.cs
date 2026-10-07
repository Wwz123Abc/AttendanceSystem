namespace AttendanceSystem.Models.Enums;

/// <summary>考勤组的审批层级：一个考勤组的申请要走一级还是二级审批。</summary>
public enum ApprovalLevelType
{
    Level1 = 1,  // 一级审批：只需要班组长审批
    Level2 = 2   // 二级审批：班组长审批通过后，再由申请人的直属上级（主管）审批
}
