using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceSystem.Models.Entities;

/// <summary>
/// 员工 ↔ 考勤机 指定分配关系（对应数据库表 UserZKDevice）：新建/编辑员工时管理员手动勾选
/// "这个人要推送到哪几台考勤机"，只有勾选过的设备才会收到这个人的工号/人脸信息，
/// 不再是无条件推给全部启用中的设备。
/// </summary>
[Table("UserZKDevice")]
public class UserZKDevice
{
    [Key] public int Id { get; set; }

    public int UserId { get; set; }
    public int ZKDeviceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [ForeignKey("UserId")]
    public User? User { get; set; }

    [ForeignKey("ZKDeviceId")]
    public ZKDevice? ZKDevice { get; set; }
}
