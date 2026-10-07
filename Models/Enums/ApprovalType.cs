namespace AttendanceSystem.Models.Enums;

// 本文件集中放“和审批有关的固定选项”。名字后面的数字是存进数据库的编号。

/// <summary>审批类型：员工提交的是哪种申请。</summary>
public enum ApprovalType
{
    PunchReplenishment = 1,  // 补卡：忘打卡了，事后申请补上
    Leave              = 2,  // 请假
    Overtime           = 3,  // 加班
    BusinessTrip       = 4   // 出差：审批通过后，出差期间自动算全勤，不用打卡
}
