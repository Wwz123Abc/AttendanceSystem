using AttendanceSystem.Models.DTOs;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 钉钉「请假」接口客户端：封装 attendance/getleavestatus（请假状态）接口。
/// 钉钉打卡结果接口（attendance/list）不含请假，请假必须走这个独立接口（需开通「考勤/假期数据」权限）。
/// </summary>
public interface IDingTalkLeaveClient
{
    /// <summary>
    /// 拉取 [from, to] 时间段内、指定钉钉用户的请假记录。
    /// 自动处理钉钉限制：单批用户 ≤100、单页 ≤20、翻页。
    /// </summary>
    Task<List<DingTalkLeaveStatus>> ListLeaveStatusAsync(
        IReadOnlyCollection<string> dingTalkUserIds,
        DateTime from, DateTime to,
        CancellationToken ct = default);
}
