using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>
/// 员工考勤核对 / 异议（2026-11-03 新增）：
/// 员工看近 7 天考勤记录、对某天提异议；管理员/文员（按部门范围）收到通知后处理并回复。
/// 异议不改考勤数据，只是工单；处理时仍走"手动补卡/手动调整"，支持改完自动关单。
/// </summary>
public interface IAttendanceDisputeService
{
    /// <summary>员工能核对的考勤日数（含今天，7 = 今天往前 6 天）。页面提示文字用。</summary>
    int VerifyDays { get; }

    /// <summary>员工核对页：近 7 个考勤日（今天往前 6 天 ~ 今天）的记录 + 这一天已有的异议。</summary>
    Task<List<AttendanceVerifyRowDto>> GetVerifyListAsync(int userId, CancellationToken ct = default);

    /// <summary>员工提交异议，返回新异议单编号。校验：7 天窗口、一天只能有一张处理中的、每天最多 3 张。</summary>
    Task<int> SubmitAsync(int userId, DateOnly workDate, AttendanceDisputeType type, string reason, string? expectedText, CancellationToken ct = default);

    /// <summary>员工撤回（只有"待处理"能撤）。</summary>
    Task CancelAsync(int userId, int disputeId, CancellationToken ct = default);

    /// <summary>管理端列表（<paramref name="allowedDeptIds"/> 为 null 表示不受部门范围限制）。</summary>
    Task<AttendanceDisputeListResultDto> ListForHandlerAsync(AttendanceDisputeQueryDto query, HashSet<int>? allowedDeptIds, CancellationToken ct = default);

    /// <summary>待处理数量（首页/菜单角标用；<paramref name="allowedDeptIds"/> 为 null 表示全部）。</summary>
    Task<int> CountPendingAsync(HashSet<int>? allowedDeptIds, CancellationToken ct = default);

    /// <summary>处理：<paramref name="accept"/>=true 记为"已处理"，false 记为"已驳回"；<paramref name="reply"/> 会通知员工。
    /// <paramref name="allowedDeptIds"/> 非空时校验该员工在范围内（分公司管理员/文员只能处理自己公司的）。</summary>
    Task HandleAsync(int disputeId, int handlerUserId, string handlerName, bool accept, string reply, HashSet<int>? allowedDeptIds = null, CancellationToken ct = default);

    /// <summary>管理员用"手动补卡/手动调整"改完某天记录后调用：把这天还在处理中的异议自动标记为已处理并通知员工。返回关掉几张。</summary>
    Task<int> AutoResolveAsync(int userId, DateOnly workDate, string note, CancellationToken ct = default);
}
