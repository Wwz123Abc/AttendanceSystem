using AttendanceSystem.Models.DTOs;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>钉钉考勤接口客户端，封装 attendance/list（含 7天/50人/50条 的拆分与翻页）。</summary>
public interface IDingTalkAttendanceClient
{
    /// <summary>
    /// 拉取指定钉钉用户在 [from, to] 时间段内的全部打卡结果。
    /// 内部自动按「≤7 天切片 × ≤50 人分批 × ≤50 条翻页」循环调用钉钉接口。
    /// </summary>
    Task<List<DingTalkRecordResult>> ListAttendanceAsync(
        IReadOnlyCollection<string> dingTalkUserIds,
        DateTime from, DateTime to,
        CancellationToken ct = default);
}
