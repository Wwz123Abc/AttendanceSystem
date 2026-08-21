using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 远程打卡的每一次人脸识别尝试（对应数据库表 FaceVerifyAttempt）：不管成功失败都记一条，
/// 一是给限流查询用（同一人短时间内失败次数太多就临时挡住，防止拿别人照片反复试），
/// 二是留一份审计记录，方便以后有争议时回查。
/// </summary>
[Table("FaceVerifyAttempt")]
public class FaceVerifyAttempt
{
    [Key] public int Id { get; set; }

    public int UserId { get; set; }

    public bool Success { get; set; }

    /// <summary>失败原因（活体没过 / 比对没过 / 接口调用失败等），成功的话是 null</summary>
    [MaxLength(300)]
    public string? FailReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [ForeignKey("UserId")]
    public User User { get; set; } = null!;
}
