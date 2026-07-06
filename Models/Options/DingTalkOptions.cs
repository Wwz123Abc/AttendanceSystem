namespace AttendanceSystem.Models.Options;

/// <summary>
/// 钉钉开放平台对接配置（绑定 appsettings.json 的 "DingTalk" 节）。
/// </summary>
public class DingTalkOptions
{
    public const string SectionName = "DingTalk";

    // ── 应用凭证 ────────────────────────────────────────────────────────────
    /// <summary>企业内部应用 AppKey</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>企业内部应用 AppSecret</summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// 应用的 AgentId（发送"工作通知"要用，跟 AppKey/AppSecret 不是一回事）。
    /// 在钉钉开发者后台该应用的"应用信息"页可以查到；需确认该应用已开通"工作通知"接口权限。
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    // ── 网关地址 ────────────────────────────────────────────────────────────
    /// <summary>旧版网关（attendance/list 打卡结果接口在此）</summary>
    public string OldGatewayBase { get; set; } = "https://oapi.dingtalk.com";

    /// <summary>新版网关（获取 accessToken 接口在此）</summary>
    public string NewGatewayBase { get; set; } = "https://api.dingtalk.com";

    // ── 钉钉接口硬性限制（做成可配置，便于钉钉调整时无需改代码）────────────
    /// <summary>单次请求时间跨度上限（天），钉钉限制 ≤ 7</summary>
    public int MaxDaysPerRequest { get; set; } = 7;

    /// <summary>单次请求用户数上限，钉钉限制 ≤ 50</summary>
    public int MaxUsersPerRequest { get; set; } = 50;

    /// <summary>单次返回条数上限（翻页页大小），钉钉限制 ≤ 50</summary>
    public int PageSize { get; set; } = 50;

    // ── 请假同步（钉钉打卡结果接口不含请假，需另调请假状态接口）─────────────
    /// <summary>同步打卡时是否一并拉取钉钉请假并写入考勤记录（需开通「考勤/假期数据」权限）</summary>
    public bool EnableLeaveSync { get; set; } = true;

    /// <summary>请假状态接口单批用户数上限，钉钉限制 ≤ 100</summary>
    public int LeaveMaxUsersPerRequest { get; set; } = 100;

    /// <summary>请假状态接口单页条数上限，钉钉限制 ≤ 20</summary>
    public int LeavePageSize { get; set; } = 20;

    // ── 定时自动同步（默认关闭）────────────────────────────────────────────
    /// <summary>是否启用后台定时自动同步</summary>
    public bool EnableScheduledSync { get; set; } = false;

    /// <summary>定时同步间隔（分钟）</summary>
    public int ScheduledSyncIntervalMinutes { get; set; } = 60;

    /// <summary>每次定时同步回溯的天数（从今天往前推几天）</summary>
    public int ScheduledSyncLookbackDays { get; set; } = 1;
}
