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

    /// <summary>非空表示这条是"被成本闸门拦截"的记录（间隔未到 / 今日次数上限），不是真的调用过阿里云接口——
    /// Success 这种行恒为 false，但跟"真失败"（FailReason 非空）是两回事，统计失败限流/今日次数时要排除掉，
    /// 免得"被拦一次"反而变成"算一次失败"、更容易触发别的限制。</summary>
    [MaxLength(50)]
    public string? BlockedReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [ForeignKey("UserId")]
    public User User { get; set; } = null!;
}
