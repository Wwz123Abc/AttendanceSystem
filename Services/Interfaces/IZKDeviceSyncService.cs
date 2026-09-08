using AttendanceSystem.Models.Entities;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>考勤机推上来的一条原始打卡记录（ATTLOG）。</summary>
/// <param name="Pin">设备上的用户编号，本系统直接用 User.EmployeeNo 当 PIN 用</param>
/// <param name="Time">打卡时间</param>
/// <param name="Status">打卡状态：0=签到/上班，1=签退/下班，2=外出，3=外出返回，其余按厂商定义</param>
/// <param name="Verify">验证方式：0=密码，1=指纹，15=人脸 等，厂商定义，目前只记录不做业务判断</param>
public record ZKAttLogRow(string Pin, DateTime Time, int Status, int Verify);

/// <summary>处理熵基考勤机推送上来的数据，落到 AttendancePunch/AttendanceRecord。</summary>
public interface IZKDeviceSyncService
{
    Task ProcessAttLogAsync(string sn, List<ZKAttLogRow> rows, CancellationToken ct = default);

    /// <summary>把员工的工号+姓名排进下发队列，下次设备心跳时会把 DATA UPDATE USERINFO 命令带给它，
    /// 在设备上预先建好档（还是需要员工本人到设备前面刷脸完成人脸录入，这一步没法远程做）。</summary>
    Task EnqueuePushUserInfoAsync(User user, CancellationToken ct = default);

    /// <summary>把"删除该工号"排进下发队列，下次设备心跳时会把 DATA DELETE USERINFO 命令带给它，
    /// 员工离职/被删除后设备上也不再认这张脸/这个工号。<paramref name="userId"/> 用来查这个人绑定过
    /// 哪些设备——调用方必须在删除 User 行（会级联删除设备绑定关系）之前调用这个方法。</summary>
    Task EnqueueDeleteUserInfoAsync(string employeeNo, int userId, CancellationToken ct = default);

    /// <summary>把"删除该工号"排进下发队列，只发给指定的这几台设备（不查这个人当前绑定了哪些设备）。
    /// 用在"编辑员工时取消勾选了某台设备"的场景：那台设备已经从关联表里解除了，查不出来了，
    /// 必须由调用方显式传入要清掉的设备 id 列表。</summary>
    Task EnqueueDeleteUserInfoForDevicesAsync(string employeeNo, IEnumerable<int> deviceIds, CancellationToken ct = default);
}
