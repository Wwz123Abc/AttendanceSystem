namespace AttendanceSystem.Helpers;

/// <summary>
/// 多个入口（页面、接口、智能助手）共用的输入上限。同一个限制只在这里写一次，
/// 数据库列长度（实体上的 <c>[MaxLength]</c>）改动时，这里要同步。
/// </summary>
public static class InputLimits
{
    /// <summary>单张图片上传上限（人脸照片、身份证照片、审批附件）：10MB。</summary>
    public const long MaxImageUploadBytes = 10 * 1024 * 1024;

    /// <summary>申请事由最长字数（请假/加班/出差/补卡）。</summary>
    public const int ApprovalReasonMaxLength = 1000;

    /// <summary>审批意见最长字数。</summary>
    public const int ApprovalCommentMaxLength = 1000;

    /// <summary>出差目的地最长字数。</summary>
    public const int BusinessTripDestinationMaxLength = 200;

    /// <summary>公告标题最长字数。</summary>
    public const int AnnouncementTitleMaxLength = 200;

    /// <summary>公告正文最长字数。</summary>
    public const int AnnouncementContentMaxLength = 2000;
}
