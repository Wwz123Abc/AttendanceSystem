using AttendanceSystem.Models.DTOs;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>钉钉打卡数据同步服务：从钉钉拉取打卡结果并落到本地库。</summary>
public interface IDingTalkSyncService
{
    /// <summary>
    /// 从钉钉拉取 [from, to] 时间段的打卡结果，写入本地原始打卡流水（去重）并汇总到考勤日记录。
    /// </summary>
    Task<DingTalkSyncResultDto> SyncAsync(DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// 按「工号」自动映射：拉取钉钉通讯录，用本地 EmployeeNo 匹配钉钉 job_number 回填 DingTalkUserId（只映射，不新建员工）。
    /// overwrite=false 只补未映射的；true 则全部重新映射。
    /// </summary>
    Task<DingTalkAutoMapResultDto> AutoMapByJobNumberAsync(bool overwrite = false, CancellationToken ct = default);

    /// <summary>
    /// 从钉钉通讯录整批导入员工：已有的按 userid/手机号对接并补全 userid，缺失的自动新建。
    /// 导入后无需再手动映射，可直接同步打卡。
    /// </summary>
    Task<DingTalkImportResultDto> ImportEmployeesAsync(CancellationToken ct = default);
}
