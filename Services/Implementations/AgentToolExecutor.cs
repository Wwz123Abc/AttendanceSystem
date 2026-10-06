using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Services.Implementations;

/// <inheritdoc cref="IAgentToolExecutor"/>
/// <remarks>
/// 工具实现原则：
/// 1. 全部只读（本阶段 M2）；写工具在 M3 以"动作提案+管理员确认"形态加入。
/// 2. 每个工具执行时都从 DB 重新读操作者范围（不信任缓存），再做范围过滤；
/// 3. 返回文本按"给模型看的最小信息"组装：姓名/工号/部门/日期/状态可以给，
///    身份证号/完整手机号/住址/照片 URL 一律不给（手机号打码）。
/// </remarks>
public class AgentToolExecutor(
    AttendanceDbContext db,
    IDeptScopeService deptScope,
    ILogger<AgentToolExecutor> logger) : IAgentToolExecutor
{
    // ── 工具定义（参数用 JSON Schema 描述，模型据此填参）───────────────────────

    public IReadOnlyList<AgentToolDefinition> GetDefinitions() =>
    [
        new("user_search",
            "按姓名/工号模糊查询在职或停用员工（自动限定在操作者管理范围内）。返回行首括号内数字为 userId（userId），后续写操作工具（如 *_propose）需要用它作为 userId 参数。另返回工号/姓名/部门/考勤组/角色/状态/打码手机号。",
            """{"type":"object","properties":{"keyword":{"type":"string","description":"姓名或工号关键字，可空=查范围内全部"},"deptId":{"type":"integer","description":"限定某部门(含其下级)，可空"},"status":{"type":"string","enum":["active","disabled","blacklisted"],"description":"在职/停用/黑名单，可空=全部"},"limit":{"type":"integer","description":"最多返回条数，默认20，上限50"}},"required":[]}"""),
        new("pending_registration_list",
            "列出操作者管理范围内、员工扫码提交后尚未确认的\"待确认登记\"（脱敏：只含姓名/打码手机号/意向部门/提交时间）。如需驳回其中某条，可用 registration_reject_propose 生成待确认动作。",
            """{"type":"object","properties":{"limit":{"type":"integer","description":"最多返回条数，默认30，上限50"}},"required":[]}"""),
        new("attendance_anomaly_list",
            "查询某日期范围内自己管理范围内的考勤异常（迟到/早退/旷工/缺卡），可只筛一种类型。日期格式 yyyy-MM-dd。",
            """{"type":"object","properties":{"start":{"type":"string","description":"开始日期 yyyy-MM-dd，必填"},"end":{"type":"string","description":"结束日期 yyyy-MM-dd，默认同 start"},"type":{"type":"string","enum":["late","early","absent","notpunched"],"description":"只筛某一种异常，可空=全部四种"},"deptId":{"type":"integer","description":"限定某部门(含其下级)，可空"},"limit":{"type":"integer","description":"最多列出条数，默认50，上限100"}},"required":["start"]}"""),
        new("monthly_summary_get",
            "查询某年月的月度考勤汇总概况与异常突出人员（自动限定在操作者管理范围内）。汇总未生成时会明确说明，不会触发重算。",
            """{"type":"object","properties":{"year":{"type":"integer","description":"年份，必填"},"month":{"type":"integer","description":"月份1-12，必填"},"limit":{"type":"integer","description":"异常突出人员最多列几条，默认20，上限50"}},"required":["year","month"]}"""),
        new("device_status_list",
            "列出操作者管理范围内的考勤机及其在线/离线状态。",
            """{"type":"object","properties":{"onlineOnly":{"type":"boolean","description":"只列在线的，可空"},"limit":{"type":"integer","description":"最多条数，默认50，上限100"}},"required":[]}"""),
        new("department_list",
            "列出操作者管理范围内的部门树（id/名称/上级部门/所属公司/绑定的考勤组）。employee_create_propose 等工具需要的 deptId 应先用这个（或 attendance_group_list）查到，不要凭空猜数字。",
            """{"type":"object","properties":{"keyword":{"type":"string","description":"部门名称关键字，可空=返回范围内全部"}},"required":[]}"""),
        new("attendance_group_list",
            "列出操作者管理范围内的考勤组（id/名称/审批层级/是否启用定位打卡）。",
            """{"type":"object","properties":{}, "required":[]}"""),
        new("punch_adjust_propose",
            "【写操作·需管理员确认】为范围内某员工某天补录/修正上下班打卡时间。此工具只生成待确认动作，不会直接修改；管理员在页面上点\"确认执行\"后才生效。",
            """{"type":"object","properties":{"userId":{"type":"integer","description":"员工 userId（先用 user_search 查到）","minimum":1},"workDate":{"type":"string","description":"补卡日期 yyyy-MM-dd，必填"},"clockIn":{"type":"string","description":"上班时间 HH:mm（如 09:05），可空"},"clockOut":{"type":"string","description":"下班时间 HH:mm（如 18:20），可空"},"remark":{"type":"string","description":"备注（如：设备故障漏打卡），可空"}},"required":["userId","workDate"]}"""),
        new("registration_reject_propose",
            "【写操作·需管理员确认】驳回某条待确认的扫码登记（先调 pending_registration_list 拿到登记 id）。只生成待确认动作，管理员确认后才执行。",
            """{"type":"object","properties":{"registrationId":{"type":"integer","description":"待确认登记 id（pending_registration_list 返回的 #号后的数字）","minimum":1},"reason":{"type":"string","description":"驳回原因，可空"}},"required":["registrationId"]}"""),
        new("user_toggle_propose",
            "【写操作·需管理员确认】停用或启用范围内某员工账号（不能操作黑名单员工，也不能停用自己）。只生成待确认动作，管理员确认后才执行。",
            """{"type":"object","properties":{"userId":{"type":"integer","description":"员工 userId","minimum":1},"action":{"type":"string","enum":["deactivate","activate"],"description":"deactivate=停用；activate=启用"}},"required":["userId","action"]}"""),
        new("user_delete_propose",
            "【高风险·写操作·需管理员确认】彻底删除某员工账号（不可恢复，历史一并清除）。只生成待确认动作；请先与管理员确认。",
            """{"type":"object","properties":{"userId":{"type":"integer","description":"员工 userId","minimum":1}},"required":["userId"]}"""),
        new("user_blacklist_propose",
            "【高风险·写操作·需管理员确认】把员工拉黑（禁止登录、工号永不再用、黑名单全公司共享）或移出黑名单。只生成待确认动作。",
            """{"type":"object","properties":{"userId":{"type":"integer","description":"员工 userId","minimum":1},"action":{"type":"string","enum":["blacklist","remove"],"description":"blacklist=拉黑；remove=移出黑名单"}},"required":["userId","action"]}"""),
        new("password_reset_propose",
            "【高风险·写操作·需管理员确认】重置某员工登录密码（系统自动生成随机密码，仅在管理员确认后的页面展示一次）。只生成待确认动作。",
            """{"type":"object","properties":{"userId":{"type":"integer","description":"员工 userId","minimum":1}},"required":["userId"]}"""),
        new("scope_change_propose",
            "【仅总部·高风险·写操作·需管理员确认】把某人设为某部门的管理范围（分公司管理员）或清空其范围（恢复不受限）。只生成待确认动作；非总部超级管理员不可用。",
            """{"type":"object","properties":{"userId":{"type":"integer","description":"目标账号 userId","minimum":1},"action":{"type":"string","enum":["set","clear"],"description":"set=指定范围(需deptId)；clear=清空范围"},"deptId":{"type":"integer","description":"action=set 时必填：管理范围部门 id"}},"required":["userId","action"]}"""),
        new("registration_confirm_propose",
            "【写操作·需管理员确认】把某条待确认的扫码登记正式建档为员工（认领登记：先调 pending_registration_list 拿登记 id；系统按所选部门自动生成工号，初始密码统一 123456，系统不会强制改密，需要提醒本人自行修改）。只生成待确认动作。",
            """{"type":"object","properties":{"registrationId":{"type":"integer","description":"待确认登记 id（pending_registration_list 返回的 #号数字）","minimum":1},"deptId":{"type":"integer","description":"员工归属部门 id（必须是 user_search/部门树里可见的部门）","minimum":1},"supervisorId":{"type":"integer","description":"直属上级 userId（须为该部门下角色=主管的在职员工）","minimum":1},"employeeNo":{"type":"string","description":"可选：手动指定工号（字母数字下划线短横线）；缺省自动生成"}},"required":["registrationId","deptId","supervisorId"]}"""),
        new("employee_create_propose",
            "【写操作·需管理员确认】普通建档：新建一名员工（无扫码登记场景）。初始密码统一 123456，系统不会强制改密，需要提醒本人自行修改。只生成待确认动作。",
            """{"type":"object","properties":{"realName":{"type":"string","description":"真实姓名，必填"},"deptId":{"type":"integer","description":"归属部门 id（范围内），必填"},"supervisorId":{"type":"integer","description":"直属上级 userId（须为该部门在职主管/班组长），必填"},"phone":{"type":"string","description":"11 位手机号，必填"},"employeeNo":{"type":"string","description":"可选工号；缺省自动生成"},"position":{"type":"string","description":"岗位，可选"},"contractCompany":{"type":"string","description":"劳务/合同公司，可选"},"hireDate":{"type":"string","description":"入职日期 yyyy-MM-dd，可选"}},"required":["realName","deptId","supervisorId","phone"]}"""),
        new("employee_update_propose",
            "【写操作·需管理员确认】修改员工资料（部门/姓名/岗位/手机号/合同公司/直属上级/入职日期）。换部门会自动跟随该部门绑定的考勤组。只生成待确认动作。",
            """{"type":"object","properties":{"userId":{"type":"integer","description":"员工 userId（范围内），必填"},"realName":{"type":"string","description":"改名，可选"},"deptId":{"type":"integer","description":"新部门 id（范围内），可选"},"supervisorId":{"type":"integer","description":"新直属上级 userId（与部门一致），可选"},"phone":{"type":"string","description":"新手机号（11 位），可选"},"position":{"type":"string","description":"新岗位，可选"},"contractCompany":{"type":"string","description":"新合同公司，可选"},"hireDate":{"type":"string","description":"入职日期 yyyy-MM-dd，可选"}},"required":["userId"]}"""),
        new("employee_role_propose",
            "【写操作·需管理员确认】调整员工角色：employee=员工 / supervisor=主管 / teamleader=班组长 / clerk=文员 / admin=管理员。注意：admin 与 clerk 只有总部超级管理员能设置（受限管理员设 clerk 会造成无范围文员=全公司权限），受限管理员只能设 员工/主管/班组长，不能改自己。只生成待确认动作。",
            """{"type":"object","properties":{"userId":{"type":"integer","description":"目标员工 userId（范围内）","minimum":1},"role":{"type":"string","enum":["employee","clerk","supervisor","teamleader","admin"],"description":"目标角色"}},"required":["userId","role"]}"""),
        new("employee_batch_toggle_propose",
            "【写操作·需管理员确认】批量停用/启用多名员工（范围内，不含自己，不含黑名单）。只生成待确认动作。",
            """{"type":"object","properties":{"userIds":{"type":"array","items":{"type":"integer"},"description":"员工 userId 列表"},"action":{"type":"string","enum":["deactivate","activate"],"description":"deactivate=批量停用；activate=批量启用"}},"required":["userIds","action"]}"""),
        new("approval_pending_list",
            "列出当前登录者作为审批人、尚未处理的审批单（含单号/申请人/类型/日期/理由）。处理后用 approval_handle_propose 生成待确认动作。",
            """{"type":"object","properties":{"limit":{"type":"integer","description":"最多条数，默认30，上限50"}},"required":[]}"""),
        new("approval_handle_propose",
            "【写操作·需管理员确认】处理指派给自己的待审批单（通过/驳回；驳回需填意见）。通过会回写考勤/请假/加班等记录。只生成待确认动作。",
            """{"type":"object","properties":{"requestId":{"type":"integer","description":"审批单 id（approval_pending_list 返回的 #号数字）","minimum":1},"approve":{"type":"boolean","description":"true=通过 false=驳回"},"comment":{"type":"string","description":"审批意见（驳回时必填，通过可空）"}},"required":["requestId","approve"]}"""),
        new("approval_submit_on_behalf_propose",
            "【写操作·需管理员确认】代范围内某员工提交一条请假/加班/出差申请（员工本人不方便操作系统时，由管理员代为录入）。提交后仍会走正常审批流程（指派给该员工的审批人，不是直接生效），管理员可在「待我审批」或「审批记录」里跟踪。如果该员工所在考勤组配置了审批人名单，必须指定 approverUserId（不指定会报错并列出可选名单，拿到名单后照着再调一次）。只生成待确认动作。",
            """{"type":"object","properties":{"userId":{"type":"integer","description":"申请人 userId（先用 user_search 查到）","minimum":1},"type":{"type":"string","enum":["leave","overtime","businesstrip"],"description":"leave=请假；overtime=加班；businesstrip=出差"},"startTime":{"type":"string","description":"开始时间，yyyy-MM-dd HH:mm，必填"},"endTime":{"type":"string","description":"结束时间，yyyy-MM-dd HH:mm，必填"},"leaveType":{"type":"string","enum":["sick","personal","annual","marriage","maternity","bereavement","compensatory"],"description":"type=leave 时必填：请假类型"},"destination":{"type":"string","description":"type=businesstrip 时必填：出差目的地"},"reason":{"type":"string","description":"申请理由，必填"},"approverUserId":{"type":"integer","description":"审批人 userId：只有该员工所在考勤组配置了审批人名单时才需要，不确定就先不填，工具会报错并给出名单","minimum":1}},"required":["userId","type","startTime","endTime","reason"]}"""),
        new("announcement_publish_propose",
            "【写操作·需管理员确认】发布系统公告（标题+正文+范围：all=全公司[仅总部]、department=部门、attendancegroup=考勤组）。会通知范围内所有在职员工。只生成待确认动作。",
            """{"type":"object","properties":{"title":{"type":"string","description":"标题（≤200字）"},"content":{"type":"string","description":"正文（≤2000字）"},"scope":{"type":"string","enum":["all","department","attendancegroup"],"description":"发布范围"},"scopeId":{"type":"integer","description":"scope=department 填部门id；attendancegroup 填考勤组id"},"audienceNote":{"type":"string","description":"可选：告诉管理员大概发给谁（如：按你给出的范围自动计算），仅作备注"}},"required":["title","content","scope"]}"""),
        new("announcement_withdraw_propose",
            "【写操作·需管理员确认】撤下一条公告（软删除；只能撤自己发的，或总部撤任意）。只生成待确认动作。",
            """{"type":"object","properties":{"announcementId":{"type":"integer","description":"公告 id（可用公告列表/发布历史得到）","minimum":1}},"required":["announcementId"]}"""),
        new("device_register_propose",
            "【写操作·需管理员确认】登记一台新考勤机（SN+别名+归属部门）。只生成待确认动作。",
            """{"type":"object","properties":{"sn":{"type":"string","description":"设备序列号（唯一，字母数字点横下划线）"},"name":{"type":"string","description":"设备别名（如：一号门闸机）"},"departmentId":{"type":"integer","description":"归属部门 id（可空=总部共用设备，仅总部可登记）"}},"required":["sn"]}"""),
        new("device_update_propose",
            "【写操作·需管理员确认】修改考勤机信息（别名/启用停用/归属部门）。只生成待确认动作。",
            """{"type":"object","properties":{"deviceId":{"type":"integer","description":"考勤机 id（device_status_list 返回里注意，如需要可先查列表拿 id）","minimum":1},"name":{"type":"string","description":"新别名，可选"},"active":{"type":"boolean","description":"true=启用 false=停用，可选"},"departmentId":{"type":"integer","description":"新归属部门 id（null 传 0 表示总部共用/未归类，仅总部可设）","minimum":0}},"required":["deviceId"]}""")
    ];

    // ── 执行入口 ─────────────────────────────────────────────────────────────

    public async Task<string> ExecuteAsync(int operatorUserId, int conversationId, string toolName, string argsJson, CancellationToken ct)
    {
        try
        {
            return toolName switch
            {
                "user_search"                 => await UserSearchAsync(operatorUserId, argsJson, ct),
                "pending_registration_list"   => await PendingRegistrationListAsync(operatorUserId, argsJson, ct),
                "attendance_anomaly_list"     => await AttendanceAnomalyListAsync(operatorUserId, argsJson, ct),
                "monthly_summary_get"         => await MonthlySummaryGetAsync(operatorUserId, argsJson, ct),
                "device_status_list"          => await DeviceStatusListAsync(operatorUserId, argsJson, ct),
                "department_list"              => await DepartmentListAsync(operatorUserId, argsJson, ct),
                "attendance_group_list"        => await AttendanceGroupListAsync(operatorUserId, ct),
                "punch_adjust_propose"        => await PunchAdjustProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "registration_reject_propose" => await RejectRegistrationProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "user_toggle_propose"         => await UserToggleProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "user_delete_propose"         => await UserDeleteProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "user_blacklist_propose"      => await UserBlacklistProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "password_reset_propose"      => await PasswordResetProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "scope_change_propose"        => await ScopeChangeProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "registration_confirm_propose" => await RegistrationConfirmProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "employee_create_propose"     => await EmployeeCreateProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "employee_update_propose"     => await EmployeeUpdateProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "employee_role_propose"       => await EmployeeRoleProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "employee_batch_toggle_propose" => await EmployeeBatchToggleProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "approval_pending_list"       => await ApprovalPendingListAsync(operatorUserId, argsJson, ct),
                "approval_handle_propose"     => await ApprovalHandleProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "approval_submit_on_behalf_propose" => await ApprovalSubmitOnBehalfProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "announcement_publish_propose" => await AnnouncementPublishProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "announcement_withdraw_propose" => await AnnouncementWithdrawProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "device_register_propose"     => await DeviceRegisterProposeAsync(operatorUserId, conversationId, argsJson, ct),
                "device_update_propose"       => await DeviceUpdateProposeAsync(operatorUserId, conversationId, argsJson, ct),
                _ => $"错误：未知工具 {toolName}"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AGENT 工具 {Tool} 执行异常（操作者 {UserId}）", toolName, operatorUserId);
            return $"错误：工具执行失败：{AgentErrorText.ForUser(ex)}";
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetQuickBriefAsync(int operatorUserId, CancellationToken ct)
    {
        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return "";

        var today = DateOnly.FromDateTime(DateTime.Now);
        var anomalyStatuses = new[] { AttendanceStatus.Late, AttendanceStatus.EarlyLeave, AttendanceStatus.Absent, AttendanceStatus.NotPunched };
        var anomalyQ = db.AttendanceRecords.AsNoTracking()
            .Where(r => r.WorkDate == today && anomalyStatuses.Contains(r.AttendanceStatus));
        if (visibleIds is not null)
            anomalyQ = anomalyQ.Where(r => r.User.DepartmentId != null && visibleIds.Contains(r.User.DepartmentId.Value));
        var anomalyCount = await anomalyQ.CountAsync(ct);

        var approvalCount = await db.ApprovalSteps.AsNoTracking()
            .CountAsync(s => s.ApproverUserId == operatorUserId && s.ApprovalStatus == ApprovalStatus.Pending, ct);

        var regQ = db.EmployeeRegistrations.AsNoTracking().Where(r => r.Status == RegistrationStatus.Pending);
        if (visibleIds is not null)
            regQ = regQ.Where(r => r.DepartmentId != null && visibleIds.Contains(r.DepartmentId.Value));
        var regCount = await regQ.CountAsync(ct);

        if (anomalyCount == 0 && approvalCount == 0 && regCount == 0) return "";

        var parts = new System.Collections.Generic.List<string>();
        if (anomalyCount > 0) parts.Add($"今日考勤异常 {anomalyCount} 条");
        if (approvalCount > 0) parts.Add($"待你审批 {approvalCount} 条");
        if (regCount > 0) parts.Add($"待确认登记 {regCount} 条");
        return "顺便先给你播报一下：" + string.Join("，", parts) + "。需要看详情可以直接问我，比如\"查一下今天的异常\"。";
    }

    // ── 通用辅助 ─────────────────────────────────────────────────────────────

    /// <summary>加载操作者并解析其可见部门集合（null=不受限，不过滤）。账号无效返回 (null, 错误文本)。</summary>
    private async Task<(HashSet<int>? visibleIds, string? error)> LoadScopeAsync(int operatorUserId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == operatorUserId)
            .Select(x => new { x.IsActive, x.Role, x.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (u is null || !u.IsActive)
            return (null, "错误：操作者账号不存在或已停用");

        var cu = new CurrentUser
        {
            UserId            = operatorUserId,
            Role              = u.Role,
            ScopedDepartmentId = u.ScopedDepartmentId
        };
        var visibleIds = await deptScope.GetVisibleDeptIdsAsync(cu);
        return (visibleIds, null);
    }

    /// <summary>手机号打码：138****1234；不合法/为空原样返回。</summary>
    private static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "-";
        var p = phone.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(p, @"^1\d{10}$")
            ? p[..3] + "****" + p[^4..]
            : p;
    }

    private static string RoleText(UserRole role) => role switch
    {
        UserRole.Admin      => "管理员",
        UserRole.Clerk      => "文员",
        UserRole.Supervisor => "主管",
        UserRole.TeamLeader => "班组长",
        _                   => "员工"
    };

    private static string UserStateText(User u) =>
        u.IsBlacklisted ? "黑名单" : u.IsActive ? "在职" : "停用";

    private static string AttStatusText(AttendanceStatus s) => s switch
    {
        AttendanceStatus.Late        => "迟到",
        AttendanceStatus.EarlyLeave  => "早退",
        AttendanceStatus.Absent      => "旷工",
        AttendanceStatus.NotPunched  => "缺卡",
        AttendanceStatus.Normal      => "正常",
        AttendanceStatus.Holiday     => "休假",
        AttendanceStatus.OnLeave     => "请假",
        AttendanceStatus.Overtime    => "加班",
        AttendanceStatus.BusinessTrip=> "出差",
        _                            => s.ToString()
    };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…（结果过长已截断，请缩小查询范围）";

    private static string? StrArg(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? IntArg(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool? BoolArg(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>按 deptId（含子树）进一步收窄；deptId 为空时返回 null=不再收窄。</summary>
    private async Task<HashSet<int>?> ResolveSubtreeAsync(HashSet<int>? visibleIds, int? deptId, CancellationToken ct)
    {
        if (!deptId.HasValue) return null;
        // 受限管理员只能看范围内部门：请求的部门若不在可见集合内直接拒绝（返回空集=查不到任何数据）
        if (visibleIds is not null && !visibleIds.Contains(deptId.Value))
            return new HashSet<int>();
        return await deptScope.GetSubtreeIdsAsync(deptId.Value);
    }

    // ── 工具 1：查员工 ───────────────────────────────────────────────────────

    private async Task<string> UserSearchAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var keyword = StrArg(args, "keyword")?.Trim();
        var status  = StrArg(args, "status");
        var limit   = Math.Clamp(IntArg(args, "limit") ?? 20, 1, 50);

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var subtree = await ResolveSubtreeAsync(visibleIds, IntArg(args, "deptId"), ct);
        if (subtree is { Count: 0 }) return "查询结果为空（请求的部门不在你的管理范围内）";

        var q = db.Users.AsNoTracking();
        if (!string.IsNullOrEmpty(keyword))
            q = q.Where(u => u.EmployeeNo.Contains(keyword) || u.RealName.Contains(keyword));
        if (visibleIds is not null)
            q = q.Where(u => u.DepartmentId != null && visibleIds.Contains(u.DepartmentId.Value));
        else if (subtree is { Count: > 0 })
            q = q.Where(u => u.DepartmentId != null && subtree.Contains(u.DepartmentId.Value));

        q = status switch
        {
            "active"      => q.Where(u => u.IsActive && !u.IsBlacklisted),
            "disabled"    => q.Where(u => !u.IsActive && !u.IsBlacklisted),
            "blacklisted" => q.Where(u => u.IsBlacklisted),
            _             => q
        };

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(u => u.EmployeeNo).Take(limit)
            .Select(u => new
            {
                u.Id, u.EmployeeNo, u.RealName, u.Role, u.IsActive, u.IsBlacklisted, u.Phone,
                DeptName = u.Department != null ? u.Department.DeptName : null,
                GroupName = u.AttendanceGroup != null ? u.AttendanceGroup.GroupName : null
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return total == 0 ? "未找到匹配的员工" : "（无更多结果）";

        var sb = new System.Text.StringBuilder();
        sb.Append($"共匹配 {total} 人，列出前 {rows.Count} 条（行首括号内数字是 userId，写操作工具需要用它）：\n");
        foreach (var r in rows)
            sb.AppendLine($"(id:{r.Id}) {r.EmployeeNo} | {r.RealName} | 部门:{(r.DeptName ?? "未分配")} | 组:{(r.GroupName ?? "-")} | {RoleText(r.Role)} | {(r.IsBlacklisted ? "黑名单" : r.IsActive ? "在职" : "停用")} | 手机:{MaskPhone(r.Phone)}");
        sb.Append("提示：如需某人的更多资料，请告知其工号，在员工档案页查看（助手不返回身份证/住址等敏感信息）。");
        return Truncate(sb.ToString(), 6000);
    }

    // ── 工具 2：待确认登记列表 ───────────────────────────────────────────────

    private async Task<string> PendingRegistrationListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var limit = Math.Clamp(IntArg(doc.RootElement, "limit") ?? 30, 1, 50);

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.EmployeeRegistrations.AsNoTracking()
            .Where(r => r.Status == RegistrationStatus.Pending);
        if (visibleIds is not null)
            q = q.Where(r => r.DepartmentId != null && visibleIds.Contains(r.DepartmentId.Value));

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.SubmittedAt).Take(limit)
            .Select(r => new { r.Id, r.RealName, r.Phone, r.SubmittedAt, DeptName = r.Department != null ? r.Department.DeptName : null })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return total == 0
                ? "当前没有待确认的登记。"
                : "（无更多结果）";

        var sb = new System.Text.StringBuilder();
        sb.Append($"共 {total} 条待确认登记，列出前 {rows.Count} 条（已脱敏）：\n");
        foreach (var r in rows)
            sb.AppendLine($"#{r.Id} {r.RealName} | 手机:{MaskPhone(r.Phone)} | 意向部门:{(r.DeptName ?? "未指定(由总部处理)")} | 提交:{r.SubmittedAt:yyyy-MM-dd HH:mm}");
        sb.Append("提示：如需驳回某条登记，可继续调用 registration_reject_propose 生成待确认动作，由管理员确认后执行。");
        return Truncate(sb.ToString(), 4000);
    }

    // ── 工具 3：考勤异常清单 ──────────────────────────────────────────────────

    private const int MaxAnomalyQueryDays = 92;

    private async Task<string> AttendanceAnomalyListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;

        var startText = StrArg(args, "start");
        if (!DateOnly.TryParse(startText, out var start))
            return "错误：start 参数格式应为 yyyy-MM-dd（如 2026-09-01）";
        var end = DateOnly.TryParse(StrArg(args, "end"), out var e) ? e : start;
        if (end < start) (start, end) = (end, start);
        // 不限跨度的话，一句"查 2000-01-01 到 2100-01-01 的全部异常"就会对 22 万行且还在增长的 AttendanceRecord
        // 做全表 count + join（2026-10-06 复核发现），超了让模型按月/按季分段查
        if (end.DayNumber - start.DayNumber + 1 > MaxAnomalyQueryDays)
            return $"错误：单次查询的日期跨度不能超过 {MaxAnomalyQueryDays} 天，请缩小范围或分段查询";
        var type  = StrArg(args, "type");
        var limit = Math.Clamp(IntArg(args, "limit") ?? 50, 1, 100);

        List<AttendanceStatus> statuses = type switch
        {
            "late"       => [AttendanceStatus.Late],
            "early"      => [AttendanceStatus.EarlyLeave],
            "absent"     => [AttendanceStatus.Absent],
            "notpunched" => [AttendanceStatus.NotPunched],
            _            => [AttendanceStatus.Late, AttendanceStatus.EarlyLeave, AttendanceStatus.Absent, AttendanceStatus.NotPunched]
        };

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var subtree = await ResolveSubtreeAsync(visibleIds, IntArg(args, "deptId"), ct);
        if (subtree is { Count: 0 }) return "查询结果为空（请求的部门不在你的管理范围内）";

        var q = db.AttendanceRecords.AsNoTracking()
            .Where(r => r.WorkDate >= start && r.WorkDate <= end && statuses.Contains(r.AttendanceStatus));
        if (visibleIds is not null)
            q = q.Where(r => r.User.DepartmentId != null && visibleIds.Contains(r.User.DepartmentId.Value));
        else if (subtree is not null)
            q = q.Where(r => r.User.DepartmentId != null && subtree.Contains(r.User.DepartmentId.Value));

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.WorkDate).ThenBy(r => r.User.RealName).Take(limit)
            .Select(r => new
            {
                r.WorkDate, r.AttendanceStatus, r.LateMinutes, r.EarlyLeaveMinutes,
                r.ClockInTime, r.ClockOutTime,
                Eno = r.User.EmployeeNo, Name = r.User.RealName
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return $"在 {start:yyyy-MM-dd} 至 {end:yyyy-MM-dd} 内未找到考勤异常记录。";

        var sb = new System.Text.StringBuilder();
        sb.Append($"{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}，异常共 {total} 条，列出前 {rows.Count} 条：\n");
        foreach (var r in rows)
        {
            var detail = new System.Collections.Generic.List<string> { AttStatusText(r.AttendanceStatus) };
            // 走全系统统一口径（只认状态本身就是迟到/早退的记录）：状态已被后台改成旷工/未打卡的记录，
            // 字段里可能还残留几百分钟，直接拼进回复会跟报表里的 0 自相矛盾（2026-10-06 复核发现）
            var lateMin  = AttendanceService.EffectiveLateMinutes(r.AttendanceStatus, r.LateMinutes);
            var earlyMin = AttendanceService.EffectiveEarlyLeaveMinutes(r.AttendanceStatus, r.EarlyLeaveMinutes);
            if (lateMin > 0) detail.Add($"迟到{lateMin}分");
            if (earlyMin > 0) detail.Add($"早退{earlyMin}分");
            var ci = r.ClockInTime?.ToString("HH:mm") ?? "-";
            var co = r.ClockOutTime?.ToString("HH:mm") ?? "-";
            sb.AppendLine($"{r.WorkDate:MM-dd} {r.Name}({r.Eno}) [{string.Join("，", detail)}] 上班{ci} 下班{co}");
        }
        return Truncate(sb.ToString(), 6000);
    }

    // ── 工具 4：月度汇总 ──────────────────────────────────────────────────────

    private async Task<string> MonthlySummaryGetAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var year  = IntArg(args, "year");
        var month = IntArg(args, "month");
        var limit = Math.Clamp(IntArg(args, "limit") ?? 20, 1, 50);
        if (!year.HasValue || !month.HasValue || month is < 1 or > 12)
            return "错误：year(如2026) 与 month(1-12) 参数必填且合法";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.MonthlyAttendanceSummaries.AsNoTracking()
            .Where(s => s.Year == year.Value && s.Month == month.Value);
        if (visibleIds is not null)
            q = q.Where(s => s.User.DepartmentId != null && visibleIds.Contains(s.User.DepartmentId.Value));

        var exists = await q.AnyAsync(ct);
        if (!exists)
            return $"{year}年{month}月还没有生成汇总数据（系统通常在月初自动生成上个月；或管理员先在\"月度报表\"页生成）。助手不会为了查询而触发全公司重算。";

        var totals = await q.GroupBy(_ => 1)
            .Select(g => new
            {
                People   = g.Count(),
                Late     = g.Sum(x => x.LateCount),
                Early    = g.Sum(x => x.EarlyLeaveCount),
                Absent   = g.Sum(x => x.AbsentDays),
                NotPunch = g.Sum(x => x.NotPunchedCount),
                Leave    = g.Sum(x => x.LeaveDays),
                WorkH    = g.Sum(x => x.TotalWorkHours),
                OverH    = g.Sum(x => x.TotalOvertimeHours)
            })
            .FirstOrDefaultAsync(ct);

        var rows = await q.OrderByDescending(s => s.LateCount + s.EarlyLeaveCount + s.AbsentDays)
            .Take(limit)
            .Select(s => new { s.LateCount, s.EarlyLeaveCount, s.AbsentDays, s.NotPunchedCount, s.LeaveDays, s.TotalWorkHours, Eno = s.User.EmployeeNo, Name = s.User.RealName })
            .ToListAsync(ct);

        var sb = new System.Text.StringBuilder();
        if (totals is not null)
            sb.AppendLine($"{year}年{month}月 汇总：覆盖 {totals.People} 人 | 迟到{totals.Late}次 早退{totals.Early}次 旷工{totals.Absent}天 缺卡{totals.NotPunch}次 请假{totals.Leave}天 总工时{totals.WorkH:0.#}h 加班{totals.OverH:0.#}h");
        sb.AppendLine($"异常最多的 {rows.Count} 人：");
        foreach (var r in rows)
            sb.AppendLine($"{r.Name}({r.Eno}) 迟到{r.LateCount} 早退{r.EarlyLeaveCount} 旷工{r.AbsentDays}天 缺卡{r.NotPunchedCount} 请假{r.LeaveDays}天 工时{r.TotalWorkHours:0.#}h");
        var first = new DateOnly(year.Value, month.Value, 1);
        var last = first.AddMonths(1).AddDays(-1);
        sb.AppendLine($"如需导出 Excel，请告知管理员打开：/Report/MonthlyReport?start={first:yyyy-MM-dd}&end={last:yyyy-MM-dd}（该页面右上角有导出按钮，助手本身不生成文件）。");
        return Truncate(sb.ToString(), 5000);
    }

    // ── 工具 5：设备状态 ──────────────────────────────────────────────────────

    private async Task<string> DeviceStatusListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var onlineOnly = BoolArg(args, "onlineOnly") ?? false;
        var limit = Math.Clamp(IntArg(args, "limit") ?? 50, 1, 100);

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.ZKDevices.AsNoTracking();
        if (visibleIds is not null)
            q = q.Where(d => d.DepartmentId != null && visibleIds.Contains(d.DepartmentId.Value));

        var threshold = DateTime.Now.AddMinutes(-5);
        if (onlineOnly)
            q = q.Where(d => d.LastSeenAt != null && d.LastSeenAt >= threshold);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(d => d.Name).ThenBy(d => d.SN).Take(limit)
            .Select(d => new { d.Id, d.SN, d.Name, d.IsActive, d.LastSeenAt, DeptName = d.Department != null ? d.Department.DeptName : null })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return total == 0 ? "范围内没有考勤机。" : "（无更多结果）";

        var sb = new System.Text.StringBuilder();
        sb.Append($"范围内考勤机共 {total} 台，列出前 {rows.Count} 台：\n");
        foreach (var d in rows)
        {
            var state = !d.LastSeenAt.HasValue ? "从未连接"
                      : d.LastSeenAt.Value >= threshold ? "在线" : "离线";
            var last = d.LastSeenAt.HasValue ? $"最近通信 {d.LastSeenAt:MM-dd HH:mm}" : "";
            sb.AppendLine($"(id:{d.Id}) {(d.Name ?? d.SN)} | SN:{d.SN} | 部门:{(d.DeptName ?? "总部/未归类")} | {(d.IsActive ? "启用" : "停用")} | {state} {last}");
        }
        return Truncate(sb.ToString(), 6000);
    }

    // ── 工具 6：部门树 ────────────────────────────────────────────────────────

    private async Task<string> DepartmentListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var keyword = StrArg(doc.RootElement, "keyword")?.Trim();

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.Departments.AsNoTracking().Where(d => d.IsActive);
        if (visibleIds is not null)
            q = q.Where(d => visibleIds.Contains(d.Id));
        if (!string.IsNullOrEmpty(keyword))
            q = q.Where(d => d.DeptName.Contains(keyword));

        var rows = await q.OrderBy(d => d.SortIndex).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.DeptName, d.ParentId, d.CompanyName, d.AttendanceGroupId,
                                ParentName = d.ParentDepartment != null ? d.ParentDepartment.DeptName : null })
            .Take(200)
            .ToListAsync(ct);

        if (rows.Count == 0) return "范围内没有匹配的部门。";

        var sb = new System.Text.StringBuilder();
        sb.Append($"范围内部门共 {rows.Count} 个（行首括号内数字是 deptId）：\n");
        foreach (var d in rows)
            sb.AppendLine($"(id:{d.Id}) {d.DeptName} | 上级:{(d.ParentName ?? "无(顶级)")} | 公司:{(d.CompanyName ?? "-")} | 绑定考勤组:{(d.AttendanceGroupId?.ToString() ?? "未绑定")}");
        return Truncate(sb.ToString(), 6000);
    }

    // ── 工具 7：考勤组列表 ────────────────────────────────────────────────────

    private async Task<string> AttendanceGroupListAsync(int operatorUserId, CancellationToken ct)
    {
        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var q = db.AttendanceGroups.AsNoTracking();
        if (visibleIds is not null)
            q = q.Where(g => db.Departments.Any(d => d.AttendanceGroupId == g.Id && visibleIds.Contains(d.Id)));

        var rows = await q.OrderBy(g => g.Id)
            .Select(g => new { g.Id, g.GroupName, g.ApprovalLevel, g.EnableLocationPunch })
            .Take(100)
            .ToListAsync(ct);

        if (rows.Count == 0) return "范围内没有考勤组。";

        var sb = new System.Text.StringBuilder();
        sb.Append($"范围内考勤组共 {rows.Count} 个（行首括号内数字是 groupId）：\n");
        foreach (var g in rows)
            sb.AppendLine($"(id:{g.Id}) {g.GroupName} | 审批层级:{(g.ApprovalLevel == ApprovalLevelType.Level2 ? "二级(班组长+主管)" : "一级(班组长)")} | 定位打卡:{(g.EnableLocationPunch ? "开启" : "关闭")}");
        return Truncate(sb.ToString(), 6000);
    }

    // ── 写工具（提案式：只落 AgentPendingAction，绝不直接改业务数据）─────────────────

    /// <summary>校验会话归属并把一条提案落库（写工具的公共入口）。</summary>
    private async Task<(AgentPendingAction? action, string? error)> CreateProposalAsync(
        int operatorUserId, int conversationId, string toolName, string paramJson, string summary, CancellationToken ct)
    {
        var convOwned = await db.AgentConversations
            .AnyAsync(c => c.Id == conversationId && c.UserId == operatorUserId && c.IsActive, ct);
        if (!convOwned)
            return (null, "错误：会话不存在或不属于你");

        var action = new AgentPendingAction
        {
            ConversationId = conversationId,
            ToolName       = toolName,
            ParamJson      = paramJson,
            SummaryText    = summary.Length > 480 ? summary[..480] : summary,
            Status         = Models.Enums.AgentActionStatus.Pending,
            CreatedBy      = operatorUserId,
            CreatedAt      = DateTime.Now,
            ExpiresAt      = DateTime.Now.AddMinutes(15)
        };
        db.AgentPendingActions.Add(action);
        await db.SaveChangesAsync(ct);
        return (action, null);
    }

    private async Task<string> PunchAdjustProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var userId    = IntArg(args, "userId");
        var workDateS = StrArg(args, "workDate");
        var clockInS  = StrArg(args, "clockIn")?.Trim();
        var clockOutS = StrArg(args, "clockOut")?.Trim();
        var remark    = StrArg(args, "remark")?.Trim();

        if (!userId.HasValue || userId.Value <= 0 || !DateOnly.TryParse(workDateS, out var workDate))
            return "错误：需要有效的 userId 与 workDate（yyyy-MM-dd）";
        if (string.IsNullOrEmpty(clockInS) && string.IsNullOrEmpty(clockOutS))
            return "错误：上班/下班打卡时间至少要填一个（HH:mm）";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (user is null) return "错误：目标员工不存在";
        if (visibleIds is not null && (user.DepartmentId is null || !visibleIds.Contains(user.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        // 补卡跟"停用/启用"等其它高风险操作一样，只查了部门范围、漏了角色层级检查——文员能借此给
        // 总部超管补卡（2026-09-30 复核发现，属于第 12 轮 S1 同一类漏洞漏掉的工具）
        var padjOp = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (padjOp is null || !Middlewares.CurrentUser.CanManageAccountCore(padjOp.Role, padjOp.ScopedDepartmentId, user.Role, user.ScopedDepartmentId))
            return "错误：无权给该账号补卡（角色层级限制）";

        var param = JsonSerializer.Serialize(new { userId = user.Id, workDate = workDate.ToString("yyyy-MM-dd"), clockIn = clockInS, clockOut = clockOutS, remark });
        var summary = $"给 {user.RealName}（{user.EmployeeNo}）补录 {workDate:yyyy-MM-dd}："
                    + $"上班{(string.IsNullOrEmpty(clockInS) ? "--" : clockInS)} / 下班{(string.IsNullOrEmpty(clockOutS) ? "--" : clockOutS)}"
                    + (string.IsNullOrEmpty(remark) ? "" : $"（{remark}）");

        var (action, perr) = await CreateProposalAsync(operatorUserId, conversationId, "punch_adjust_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{action!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」才会真正补卡；15 分钟内有效。";
    }

    private async Task<string> RejectRegistrationProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var registrationId = IntArg(args, "registrationId");
        var reason = StrArg(args, "reason")?.Trim();
        if (!registrationId.HasValue || registrationId.Value <= 0)
            return "错误：需要有效的 registrationId";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var reg = await db.EmployeeRegistrations.AsNoTracking()
            .Where(r => r.Id == registrationId.Value)
            .Select(r => new { r.Id, r.RealName, r.DepartmentId, r.Status })
            .FirstOrDefaultAsync(ct);
        if (reg is null) return "错误：该登记不存在";
        if (reg.Status != RegistrationStatus.Pending) return "错误：该登记已不是待确认状态";
        if (visibleIds is not null && (reg.DepartmentId is null || !visibleIds.Contains(reg.DepartmentId.Value)))
            return "错误：该登记不在你的管理范围内（无部门归属的登记由总部处理）";

        var param = JsonSerializer.Serialize(new { registrationId = reg.Id, reason });
        var summary = $"驳回待确认登记 #{reg.Id}（{reg.RealName}）" + (string.IsNullOrEmpty(reason) ? "" : $"：{reason}");

        var (action, perr) = await CreateProposalAsync(operatorUserId, conversationId, "registration_reject_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{action!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」才会真正驳回；15 分钟内有效。";
    }

    private async Task<string> UserToggleProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var args = doc.RootElement;
        var userId = IntArg(args, "userId");
        var action = StrArg(args, "action");
        if (!userId.HasValue || userId.Value <= 0 || action is not ("deactivate" or "activate"))
            return "错误：需要有效的 userId 与 action（deactivate=停用 / activate=启用）";
        if (userId.Value == operatorUserId)
            return "错误：不能对自己发起停用/启用操作，请在员工管理页处理";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.IsActive, u.IsBlacklisted, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (user is null) return "错误：目标员工不存在";
        if (visibleIds is not null && (user.DepartmentId is null || !visibleIds.Contains(user.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        var toggleOp = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (toggleOp is null || !Middlewares.CurrentUser.CanManageAccountCore(toggleOp.Role, toggleOp.ScopedDepartmentId, user.Role, user.ScopedDepartmentId))
            return "错误：无权操作该账号（角色层级限制）";

        if (user.IsBlacklisted) return "错误：黑名单员工请用员工管理页的专门功能";
        if (action == "deactivate" && !user.IsActive) return "错误：该员工已是停用状态";
        if (action == "activate" && user.IsActive) return "错误：该员工本来就在职";

        var param = JsonSerializer.Serialize(new { userId = user.Id, action });
        var summary = $"{(action == "deactivate" ? "停用" : "启用")}员工 {user.RealName}（{user.EmployeeNo}）";

        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "user_toggle_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」才会真正{(action == "deactivate" ? "停用" : "启用")}；15 分钟内有效。";
    }

    // ── 高风险提案（删除/拉黑/重置密码/改管理范围——同样只落提案）────────────────────

    /// <summary>提案级校验：目标员工存在且非本人且在范围内、且操作者管得到这个角色（角色层级限制，
    /// 跟确认执行时 UserService.EnsureCanManageAsync 同一套口径——以前生成待确认动作这一步完全不查，
    /// 文员对总部超管发起重置密码/删除/拉黑照样能生成一张卡片，只是确认时才报错，容易误导管理员
    /// 以为这个操作是被允许的，2026-09-29 第 12 轮审查发现）；返回 (userId, 名字, 错误)。</summary>
    private async Task<(int? userId, string? display, string? error)> ResolveTargetAsync(
        int operatorUserId, HashSet<int>? visibleIds, int? rawUserId)
    {
        if (!rawUserId.HasValue || rawUserId.Value <= 0)
            return (null, null, "错误：需要有效的 userId（先用 user_search 查询）");
        if (rawUserId.Value == operatorUserId)
            return (null, null, "错误：不能对自己执行该操作，请在员工管理页处理");

        var u = await db.Users.AsNoTracking()
            .Where(x => x.Id == rawUserId.Value)
            .Select(x => new { x.Id, x.RealName, x.EmployeeNo, x.DepartmentId, x.Role, x.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        if (u is null) return (null, null, "错误：目标员工不存在");
        if (visibleIds is not null && (u.DepartmentId is null || !visibleIds.Contains(u.DepartmentId.Value)))
            return (null, null, "错误：该员工不在你的管理范围内");

        var op = await db.Users.AsNoTracking()
            .Where(x => x.Id == operatorUserId)
            .Select(x => new { x.Role, x.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        if (op is null || !Middlewares.CurrentUser.CanManageAccountCore(op.Role, op.ScopedDepartmentId, u.Role, u.ScopedDepartmentId))
            return (null, null, "错误：无权操作该账号（角色层级限制）");

        return (u.Id, $"{u.RealName}（{u.EmployeeNo}）", null);
    }

    private async Task<string> UserDeleteProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var rawId = IntArg(doc.RootElement, "userId");

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var (userId, display, rerr) = await ResolveTargetAsync(operatorUserId, visibleIds, rawId);
        if (rerr is not null) return rerr;

        var isBlacklisted = await db.Users.Where(u => u.Id == userId!.Value).Select(u => u.IsBlacklisted).FirstOrDefaultAsync(ct);
        if (isBlacklisted) return "错误：黑名单员工请先移出黑名单（保留记录防重复用工）";

        var param = JsonSerializer.Serialize(new { userId });
        var summary = $"【高风险】彻底删除员工 {display}（不可恢复，历史一并清除）";
        var (action, perr) = await CreateProposalAsync(operatorUserId, conversationId, "user_delete_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{action!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> UserBlacklistProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var rawId  = IntArg(doc.RootElement, "userId");
        var action = StrArg(doc.RootElement, "action");
        if (action is not ("blacklist" or "remove"))
            return "错误：需要 action（blacklist=拉黑 / remove=移出黑名单）";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var (userId, display, rerr) = await ResolveTargetAsync(operatorUserId, visibleIds, rawId);
        if (rerr is not null) return rerr;

        var state = await db.Users.Where(u => u.Id == userId!.Value)
            .Select(u => new { u.IsActive, u.IsBlacklisted }).FirstOrDefaultAsync(ct);
        if (action == "blacklist" && state!.IsBlacklisted) return "错误：该员工已在黑名单";
        if (action == "remove" && !state!.IsBlacklisted) return "错误：该员工不在黑名单";

        var param = JsonSerializer.Serialize(new { userId, action });
        var verb = action == "blacklist" ? "拉黑" : "移出黑名单";
        var summary = $"【高风险】{verb}员工 {display}（拉黑=禁止登录、工号永不再用且全公司共享）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "user_blacklist_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> PasswordResetProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var rawId = IntArg(doc.RootElement, "userId");

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var (userId, display, rerr) = await ResolveTargetAsync(operatorUserId, visibleIds, rawId);
        if (rerr is not null) return rerr;

        var param = JsonSerializer.Serialize(new { userId });
        var summary = $"【高风险】重置员工 {display} 的登录密码（系统自动生成随机密码，仅确认页面展示一次）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "password_reset_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」后查看一次性新密码；15 分钟内有效。";
    }

    private async Task<string> ScopeChangeProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root  = doc.RootElement;
        var rawId = IntArg(root, "userId");
        var action = StrArg(root, "action");
        var deptId = IntArg(root, "deptId");

        // 仅总部超级管理员（角色 Admin 且自己不受范围限制）
        var op = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        if (op is null || op.Role != AttendanceSystem.Models.Enums.UserRole.Admin || op.ScopedDepartmentId.HasValue)
            return "错误：只有总部超级管理员可以调整管理范围";
        if (action is not ("set" or "clear")) return "错误：需要 action（set=指定范围 / clear=清空范围）";
        if (!rawId.HasValue || rawId.Value <= 0) return "错误：需要有效的 userId";
        if (rawId.Value == operatorUserId) return "错误：不能调整自己的管理范围，请联系另一位总部管理员处理";

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == rawId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo })
            .FirstOrDefaultAsync();
        if (target is null) return "错误：目标账号不存在";

        string? deptName = null;
        int? affectedCount = null;
        if (action == "set")
        {
            if (!deptId.HasValue) return "错误：action=set 时必须提供 deptId";
            deptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync();
            if (deptName is null) return "错误：指定的部门不存在";
            var subtree = await deptScope.GetSubtreeIdsAsync(deptId.Value);
            affectedCount = await db.Users.CountAsync(u => u.DepartmentId != null && subtree.Contains(u.DepartmentId.Value), ct);
        }

        var param = JsonSerializer.Serialize(new { userId = target.Id, action, deptId = action == "set" ? deptId : (int?)null });
        var summary = action == "set"
            ? $"【仅总部·高风险】把 {target.RealName}（{target.EmployeeNo}）设为管理范围【{deptName}】（该部门及下级共 {affectedCount} 名员工将进入其可见/可管范围）"
            : $"【仅总部·高风险】清空 {target.RealName}（{target.EmployeeNo}）的管理范围（恢复不受限，可见/可管全公司）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "scope_change_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」；15 分钟内有效。";
    }

    // 认领登记并建档（提案版）：校验登记/部门/上级都在范围内
    private async Task<string> RegistrationConfirmProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root         = doc.RootElement;
        var registrationId = IntArg(root, "registrationId");
        var deptId       = IntArg(root, "deptId");
        var supervisorId = IntArg(root, "supervisorId");
        var employeeNo   = StrArg(root, "employeeNo")?.Trim();
        if (!registrationId.HasValue || !deptId.HasValue || !supervisorId.HasValue)
            return "错误：需要 registrationId、deptId、supervisorId（先用 pending_registration_list / user_search 查）";
        if (!string.IsNullOrEmpty(employeeNo) &&
            !System.Text.RegularExpressions.Regex.IsMatch(employeeNo, @"^[A-Za-z0-9_-]{1,50}$"))
            return "错误：工号只能包含字母/数字/下划线/短横线";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var reg = await db.EmployeeRegistrations.AsNoTracking()
            .Where(r => r.Id == registrationId.Value)
            .Select(r => new { r.Id, r.RealName, r.DepartmentId, r.Status })
            .FirstOrDefaultAsync(ct);
        if (reg is null) return "错误：该登记不存在";
        if (reg.Status != RegistrationStatus.Pending) return "错误：该登记已不是待确认状态";
        // 无部门归属的登记只有总部能建档（部门由总部指定）
        if (visibleIds is not null && (reg.DepartmentId is null || !visibleIds.Contains(reg.DepartmentId.Value)))
            return "错误：该登记不在你的管理范围内";
        if (visibleIds is not null && !visibleIds.Contains(deptId.Value))
            return "错误：归属部门不在你的管理范围内";

        var deptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct);
        if (deptName is null) return "错误：归属部门不存在";
        if (supervisorId.Value == operatorUserId)
            return "错误：直属上级不能是自己，请选该部门的主管";
        var sup = await db.Users.AsNoTracking()
            .Where(u => u.Id == supervisorId.Value)
            .Select(u => new { u.IsActive, u.Role, u.DepartmentId, u.RealName })
            .FirstOrDefaultAsync(ct);
        if (sup is null || !sup.IsActive || sup.Role != UserRole.Supervisor)
            return "错误：直属上级必须是在职的主管（角色=主管）";
        if (sup.DepartmentId != deptId.Value)
            return "错误：直属上级必须和员工在同一个部门";

        var param = JsonSerializer.Serialize(new { registrationId = reg.Id, deptId = deptId.Value, supervisorId = supervisorId.Value, employeeNo });
        var summary = $"把待确认登记 #{reg.Id}（{reg.RealName}）建档为【{deptName}】员工"
                    + (string.IsNullOrEmpty(employeeNo) ? "（工号自动生成）" : $"（工号 {employeeNo}）")
                    + "，初始密码 123456";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "registration_confirm_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员在当前页面点「确认执行」才会真正建号；15 分钟内有效。";
    }


    // 角色调整（提案）：employee/clerk/supervisor/teamleader/admin
    private async Task<string> EmployeeRoleProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root  = doc.RootElement;
        var uid   = IntArg(root, "userId");
        var roleS = StrArg(root, "role");
        UserRole? newRole = roleS switch
        {
            "employee"   => UserRole.Employee,
            "clerk"      => UserRole.Clerk,
            "supervisor" => UserRole.Supervisor,
            "teamleader" => UserRole.TeamLeader,
            "admin"      => UserRole.Admin,
            _            => null
        };
        if (!uid.HasValue || uid.Value <= 0) return "错误：需要 userId";
        if (!newRole.HasValue) return "错误：role 应为 employee/clerk/supervisor/teamleader/admin";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        if (uid.Value == operatorUserId) return "错误：不能调整自己的角色，请联系总部管理员";

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == uid.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (target is null) return "错误：目标员工不存在";
        if (visibleIds is not null && (target.DepartmentId is null || !visibleIds.Contains(target.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        var op = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        var isHq = op is { Role: UserRole.Admin } && op.ScopedDepartmentId is null;
        // 权限闸：admin/clerk 只有总部（不受限且 Admin 角色）能设
        if ((newRole is UserRole.Admin or UserRole.Clerk) && !isHq)
            return "错误：管理员/文员角色只有总部超级管理员能设置（受限管理员不能创建无范围文员）";
        // 反过来也要挡：目标现在就是管理员的话，改成别的角色（等于剥夺他的管理权限）同样只有总部超级管理员能做，
        // 跟 CanManageAccount 同一套角色层级（2026-09-29 审查发现，配合下面的 role 大小写修复一起改，
        // 不然大小写一修好，文员就能把总部管理员直接降级成普通员工）
        if (op is null || !Middlewares.CurrentUser.CanManageAccountCore(op.Role, op.ScopedDepartmentId, target.Role, target.ScopedDepartmentId))
            return "错误：无权调整该账号的角色（角色层级限制）";

        var param = JsonSerializer.Serialize(new { userId = target.Id, role = newRole.Value.ToString().ToLowerInvariant() });
        var summary = $"调整员工 {target.RealName}（{target.EmployeeNo}）角色：{RoleText(target.Role)} → {RoleText(newRole.Value)}";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "employee_role_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    // ── 审批（读：待我审批 / 提案：处理） ───────────────────────────────────────

    private async Task<string> ApprovalPendingListAsync(int operatorUserId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var limit = Math.Clamp(IntArg(doc.RootElement, "limit") ?? 30, 1, 50);

        var rows = await db.ApprovalSteps.AsNoTracking()
            .Where(s => s.ApproverUserId == operatorUserId && s.ApprovalStatus == ApprovalStatus.Pending)
            .OrderByDescending(s => s.ApprovalRequest.SubmittedAt)
            .Take(limit)
            .Select(s => new
            {
                s.Id,
                s.ApprovalRequestId,
                ReqNo = s.ApprovalRequest.RequestNo,
                Type = s.ApprovalRequest.ApprovalType,
                Applicant = s.ApprovalRequest.Applicant.RealName,
                Eno = s.ApprovalRequest.Applicant.EmployeeNo,
                Reason = s.ApprovalRequest.Reason,
                s.ApprovalRequest.SubmittedAt,
                s.ApprovalRequest.PunchDate,
                s.ApprovalRequest.LeaveStartTime,
                s.ApprovalRequest.LeaveEndTime,
                s.ApprovalRequest.OvertimeStartTime,
                s.ApprovalRequest.OvertimeEndTime,
                s.ApprovalRequest.BusinessTripStartTime,
                s.ApprovalRequest.BusinessTripEndTime,
                s.ApprovalRequest.BusinessTripDestination
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return "当前没有待你审批的单子。";

        var sb = new System.Text.StringBuilder();
        sb.Append($"共 {rows.Count} 张待你审批（用 approval_handle_propose 处理，requestId 见 #号）：\n");
        foreach (var r in rows)
        {
            string range = r.Type switch
            {
                ApprovalType.PunchReplenishment => $"补卡日期:{r.PunchDate:yyyy-MM-dd}",
                ApprovalType.Leave              => $"时间:{r.LeaveStartTime:MM-dd HH:mm}~{r.LeaveEndTime:MM-dd HH:mm}",
                ApprovalType.Overtime           => $"时间:{r.OvertimeStartTime:MM-dd HH:mm}~{r.OvertimeEndTime:MM-dd HH:mm}",
                _                               => $"时间:{r.BusinessTripStartTime:MM-dd HH:mm}~{r.BusinessTripEndTime:MM-dd HH:mm}{(string.IsNullOrEmpty(r.BusinessTripDestination) ? "" : "，去 " + r.BusinessTripDestination)}"
            };
            var reasonPreview = (r.Reason ?? "").Length > 30 ? r.Reason![..30] + "…" : (r.Reason ?? "-");
            sb.AppendLine($"#{r.ApprovalRequestId} [{r.ReqNo}] {r.Applicant}({r.Eno}) {r.Type.ToDisplayName()} {range} 理由:{reasonPreview} 提交:{r.SubmittedAt:MM-dd HH:mm}");
        }
        return Truncate(sb.ToString(), 6000);
    }

    private async Task<string> ApprovalHandleProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var reqId = IntArg(root, "requestId");
        var approve = root.TryGetProperty("approve", out var av) && av.ValueKind is JsonValueKind.True or JsonValueKind.False ? av.GetBoolean() : (bool?)null;
        var comment = StrArg(root, "comment")?.Trim();
        if (!reqId.HasValue || !approve.HasValue) return "错误：需要 requestId 与 approve(true=通过/false=驳回)";
        if (!approve.Value && string.IsNullOrWhiteSpace(comment)) return "错误：驳回时请填写意见（comment）";
        if (!string.IsNullOrEmpty(comment) && comment.Length > 1000) return "错误：意见不能超过 1000 字";

        var mine = await db.ApprovalSteps.AsNoTracking()
            .Where(s => s.ApprovalRequestId == reqId.Value && s.ApproverUserId == operatorUserId
                     && s.ApprovalStatus == ApprovalStatus.Pending)
            .Select(s => new
            {
                s.ApprovalRequest.RequestNo,
                Applicant = s.ApprovalRequest.Applicant.RealName,
                Eno = s.ApprovalRequest.Applicant.EmployeeNo,
                Type = s.ApprovalRequest.ApprovalType
            })
            .FirstOrDefaultAsync(ct);
        if (mine is null) return "错误：没有找到指派给你且待处理的这张单（可能已被处理）";

        var param = JsonSerializer.Serialize(new { requestId = reqId.Value, approve = approve.Value, comment });
        var verb = approve.Value ? "通过" : "驳回";
        var summary = $"{verb}审批单 #{reqId.Value} [{mine.RequestNo}] {mine.Applicant}（{mine.Eno}）的{mine.Type.ToDisplayName()}"
                    + (string.IsNullOrEmpty(comment) ? "" : $"（意见：{comment}）");
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "approval_handle_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    /// <summary>代员工提交请假/加班/出差申请：只生成提案；真正提交（进入正常审批流程）在管理员点确认执行时才发生。</summary>
    private async Task<string> ApprovalSubmitOnBehalfProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var userId = IntArg(root, "userId");
        var type = StrArg(root, "type");
        var startS = StrArg(root, "startTime");
        var endS = StrArg(root, "endTime");
        var leaveTypeS = StrArg(root, "leaveType");
        var destination = StrArg(root, "destination")?.Trim();
        var reason = StrArg(root, "reason")?.Trim();

        if (!userId.HasValue || userId.Value <= 0) return "错误：需要有效的 userId";
        if (type is not ("leave" or "overtime" or "businesstrip")) return "错误：type 需为 leave/overtime/businesstrip";
        if (!DateTime.TryParse(startS, out var start) || !DateTime.TryParse(endS, out var end))
            return "错误：startTime/endTime 需为 yyyy-MM-dd HH:mm 格式";
        if (end <= start) return "错误：结束时间必须晚于开始时间";
        if (string.IsNullOrWhiteSpace(reason)) return "错误：reason（申请理由）必填";
        if (type == "businesstrip" && string.IsNullOrWhiteSpace(destination)) return "错误：type=businesstrip 时 destination（出差目的地）必填";

        LeaveType? leaveType = null;
        if (type == "leave")
        {
            leaveType = leaveTypeS switch
            {
                "sick" => LeaveType.SickLeave, "personal" => LeaveType.PersonalLeave, "annual" => LeaveType.AnnualLeave,
                "marriage" => LeaveType.MarriageLeave, "maternity" => LeaveType.MaternityLeave,
                "bereavement" => LeaveType.BereavementLeave, "compensatory" => LeaveType.CompensatoryLeave,
                _ => (LeaveType?)null
            };
            if (leaveType is null) return "错误：type=leave 时 leaveType 需为 sick/personal/annual/marriage/maternity/bereavement/compensatory 之一";
        }

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.AttendanceGroupId, u.IsActive, u.IsBlacklisted })
            .FirstOrDefaultAsync(ct);
        if (target is null) return "错误：目标员工不存在";
        if (!target.IsActive || target.IsBlacklisted) return "错误：该员工已停用或在黑名单中，不能代为提交申请";
        if (visibleIds is not null && (target.DepartmentId is null || !visibleIds.Contains(target.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        // 该员工所在考勤组如果配了审批人名单，代提交必须指定其中一个（跟 SubmitApprovalAsync 里
        // CreateApprovalStepsAsync 的口径一致：配了名单就必须选，不能自动退回直属上级）；不指定
        // 会在这里直接报错并给出名单，而不是让确认执行的时候才报"请选择有效的审批人"
        // （2026-09-29 审查发现 M1：以前完全没传这个参数，配了审批人名单的考勤组代提交必然失败）
        var groupApprovers = target.AttendanceGroupId.HasValue
            ? await db.AttendanceGroupApprovers.Where(a => a.AttendanceGroupId == target.AttendanceGroupId.Value)
                .Include(a => a.Approver).Where(a => a.Approver.IsActive)
                .Select(a => new { a.UserId, a.Approver.RealName, a.Approver.EmployeeNo }).ToListAsync(ct)
            : [];
        var approverUserId = IntArg(root, "approverUserId");
        if (groupApprovers.Count > 0)
        {
            if (!approverUserId.HasValue || approverUserId.Value == target.Id
                || !groupApprovers.Any(a => a.UserId == approverUserId.Value))
            {
                var options = string.Join("、", groupApprovers.Select(a => $"{a.RealName}（{a.EmployeeNo}，userId:{a.UserId}）"));
                return $"错误：该员工所在考勤组配置了审批人名单，代提交必须指定其中一位（approverUserId），可选：{options}";
            }
        }

        var param = JsonSerializer.Serialize(new
        {
            userId = target.Id, type, startTime = start.ToString("yyyy-MM-dd HH:mm"), endTime = end.ToString("yyyy-MM-dd HH:mm"),
            leaveType = leaveType.HasValue ? (int)leaveType.Value : (int?)null, destination, reason, approverUserId
        });
        var typeText = type switch { "leave" => $"请假（{LeaveTypeText(leaveType!.Value)}）", "overtime" => "加班", _ => $"出差（{destination}）" };
        var summary = $"代 {target.RealName}（{target.EmployeeNo}）提交{typeText}申请：{start:MM-dd HH:mm} ~ {end:MM-dd HH:mm}，理由：{reason}"
                    + "（提交后仍需走正常审批流程，不会直接生效）";

        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "approval_submit_on_behalf_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」才会真正提交；15 分钟内有效。";
    }

    private static string LeaveTypeText(LeaveType t) => t switch
    {
        LeaveType.SickLeave => "病假", LeaveType.PersonalLeave => "事假", LeaveType.AnnualLeave => "年假",
        LeaveType.MarriageLeave => "婚假", LeaveType.MaternityLeave => "产假", LeaveType.BereavementLeave => "丧假",
        LeaveType.CompensatoryLeave => "调休", _ => t.ToString()
    };

    // ── 公告：发布 / 撤回（提案） ───────────────────────────────────────────────

    /// <summary>读操作者是否为总部（不受限且 Admin）。</summary>
    private async Task<bool> IsHqOperatorAsync(int operatorUserId)
    {
        var op = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync();
        return op is { Role: UserRole.Admin } && op.ScopedDepartmentId is null;
    }

    /// <summary>考勤组受众安全：组内所有在职成员的部门都必须落在操作者可见范围（无部门成员视为总部共享）。</summary>
    private async Task<(bool ok, string? err)> GroupAudienceSafeAsync(int groupId, HashSet<int>? visibleIds)
    {
        if (visibleIds is null) return (true, null);
        var memberDepts = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.AttendanceGroupId == groupId)
            .Select(u => u.DepartmentId)
            .ToListAsync();
        if (memberDepts.Any(d => !d.HasValue || !visibleIds.Contains(d.Value)))
            return (false, "该考勤组包含范围外/无部门的成员，发布会把公告发给范围外的人；请改用部门范围或联系总部");
        return (true, null);
    }

    private async Task<string> AnnouncementPublishProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var title = StrArg(root, "title")?.Trim();
        var content = StrArg(root, "content")?.Trim();
        var scope = StrArg(root, "scope");
        var scopeId = IntArg(root, "scopeId");
        if (string.IsNullOrEmpty(title)) return "错误：需要 title";
        if (title.Length > 200) return "错误：标题不能超过 200 字";
        if (string.IsNullOrEmpty(content)) return "错误：需要 content";
        if (content.Length > 2000) return "错误：正文不能超过 2000 字";
        if (scope is not ("all" or "department" or "attendancegroup")) return "错误：scope 应为 all/department/attendancegroup";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var isHq = await IsHqOperatorAsync(operatorUserId);

        string scopeText;
        if (scope == "all")
        {
            if (!isHq) return "错误：全公司公告只有总部能发";
            scopeText = "全公司";
        }
        else if (scope == "department")
        {
            if (!scopeId.HasValue) return "错误：department 范围需要 scopeId(部门id)";
            var deptName = await db.Departments.Where(d => d.Id == scopeId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct);
            if (deptName is null) return "错误：部门不存在";
            if (visibleIds is not null && !visibleIds.Contains(scopeId.Value)) return "错误：该部门不在你的管理范围内";
            scopeText = $"部门【{deptName}】（含下级）";
        }
        else
        {
            if (!scopeId.HasValue) return "错误：attendancegroup 范围需要 scopeId(考勤组id)";
            var g = await db.AttendanceGroups.Where(x => x.Id == scopeId.Value).Select(x => x.GroupName).FirstOrDefaultAsync(ct);
            if (g is null) return "错误：考勤组不存在";
            if (visibleIds is not null && !await GroupWritableAsync(scopeId.Value, visibleIds)) return "错误：无权给该考勤组发公告（含范围外部门/成员）";
            var (ok, verr) = await GroupAudienceSafeAsync(scopeId.Value, visibleIds);
            if (!ok) return $"错误：{verr}";
            scopeText = $"考勤组【{g}】";
        }

        var titlePreview = title.Length > 30 ? title[..30] + "…" : title;
        var param = JsonSerializer.Serialize(new { title, content, scope, scopeId });
        var summary = $"发布公告《{titlePreview}》到{scopeText}（将通知范围内所有在职员工）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "announcement_publish_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> AnnouncementWithdrawProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var annId = IntArg(doc.RootElement, "announcementId");
        if (!annId.HasValue) return "错误：需要 announcementId";

        var isHq = await IsHqOperatorAsync(operatorUserId);
        var ann = await db.Announcements.AsNoTracking()
            .Where(a => a.Id == annId.Value)
            .Select(a => new { a.Id, a.Title, a.PublisherUserId, a.IsActive })
            .FirstOrDefaultAsync();
        if (ann is null) return "错误：公告不存在";
        if (ann.PublisherUserId != operatorUserId && !isHq)
            return "错误：只能撤回自己发布的公告（总部可撤回任意公告）";

        var titlePreview = ann.Title.Length > 30 ? ann.Title[..30] + "…" : ann.Title;
        var param = JsonSerializer.Serialize(new { announcementId = ann.Id });
        var summary = $"撤下公告《{titlePreview}》";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "announcement_withdraw_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    // ── 考勤机：登记 / 变更（提案） ─────────────────────────────────────────────

    private async Task<string> DeviceRegisterProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var sn = StrArg(root, "sn")?.Trim();
        var name = StrArg(root, "name")?.Trim();
        var deptId = IntArg(root, "departmentId");
        if (string.IsNullOrEmpty(sn)) return "错误：需要 sn（序列号）";
        if (!System.Text.RegularExpressions.Regex.IsMatch(sn, @"^[A-Za-z0-9._-]{1,50}$"))
            return "错误：序列号只能包含字母/数字/点/横线/下划线（≤50）";
        if (await db.ZKDevices.AnyAsync(d => d.SN == sn)) return "错误：该序列号已登记";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        if (visibleIds is not null)
        {
            if (!deptId.HasValue) return "错误：受限管理员登记设备必须指定归属部门";
            if (!visibleIds.Contains(deptId.Value)) return "错误：归属部门不在你的管理范围内";
        }
        else if (deptId.HasValue)
        {
            var ok = await db.Departments.AnyAsync(d => d.Id == deptId.Value);
            if (!ok) return "错误：归属部门不存在";
        }

        var param = JsonSerializer.Serialize(new { sn, name, departmentId = deptId });
        var deptName = deptId.HasValue ? await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct) : null;
        var summary = $"登记考勤机 SN:{sn}" + (string.IsNullOrEmpty(name) ? "" : $"（{name}）") + (deptName is not null ? $" → 归属【{deptName}】" : "（总部共用/未归类）");
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "device_register_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> DeviceUpdateProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var deviceId = IntArg(root, "deviceId");
        var name = StrArg(root, "name")?.Trim();
        var active = root.TryGetProperty("active", out var av) && av.ValueKind is JsonValueKind.True or JsonValueKind.False ? av.GetBoolean() : (bool?)null;
        var deptParam = IntArg(root, "departmentId");   // 0 → null（总部共用）
        int? newDept = deptParam.HasValue && deptParam.Value == 0 ? null : deptParam;
        if (!deviceId.HasValue) return "错误：需要 deviceId";
        if (name is null && !active.HasValue && !deptParam.HasValue) return "错误：至少提供 name/active/departmentId 之一";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var d = await db.ZKDevices.AsNoTracking()
            .Where(x => x.Id == deviceId.Value)
            .Select(x => new { x.Id, x.SN, x.Name, x.DepartmentId })
            .FirstOrDefaultAsync(ct);
        if (d is null) return "错误：考勤机不存在";
        if (visibleIds is not null)
        {
            if (d.DepartmentId is null || !visibleIds.Contains(d.DepartmentId.Value))
                return "错误：该考勤机（未归类或其它范围）不在你的管理范围内";
            if (newDept.HasValue && !visibleIds.Contains(newDept.Value))
                return "错误：新归属部门不在你的管理范围内";
        }
        else if (newDept.HasValue && !await db.Departments.AnyAsync(x => x.Id == newDept.Value))
            return "错误：新归属部门不存在";

        var changes = new System.Collections.Generic.List<string>();
        if (name is not null) changes.Add("改别名");
        if (active.HasValue) changes.Add(active.Value ? "启用" : "停用");
        if (deptParam.HasValue)
        {
            var dn = newDept.HasValue ? await db.Departments.Where(x => x.Id == newDept.Value).Select(x => x.DeptName).FirstOrDefaultAsync(ct) : null;
            changes.Add(dn is not null ? $"归属→【{dn}】" : "归属→总部共用/未归类");
        }

        var param = JsonSerializer.Serialize(new { deviceId = d.Id, name, active, departmentId = newDept });
        var summary = $"修改考勤机 {(d.Name ?? d.SN)}（SN:{d.SN}）：{string.Join("、", changes)}";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "device_update_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    // ── 员工：普通建档 / 改资料 / 批量停启用（提案） ────────────────────────────

    /// <summary>校验直属上级：在职 且 角色为主管/班组长 且与部门一致。</summary>
    private async Task<(bool ok, string? err)> ValidateSupervisorAsync(int supervisorId, int deptId)
    {
        var sup = await db.Users.AsNoTracking()
            .Where(u => u.Id == supervisorId)
            .Select(u => new { u.IsActive, u.Role, u.DepartmentId })
            .FirstOrDefaultAsync();
        if (sup is null || !sup.IsActive || sup.Role is not (UserRole.Supervisor or UserRole.TeamLeader))
            return (false, "直属上级必须是在职的主管/班组长");
        if (sup.DepartmentId != deptId)
            return (false, "直属上级必须和目标员工在同一个部门");
        return (true, null);
    }

    private async Task<string> EmployeeCreateProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var realName = StrArg(root, "realName")?.Trim();
        var deptId   = IntArg(root, "deptId");
        var supId    = IntArg(root, "supervisorId");
        var phone    = StrArg(root, "phone")?.Trim();
        var employeeNo = StrArg(root, "employeeNo")?.Trim();
        var position = StrArg(root, "position")?.Trim();
        var contract = StrArg(root, "contractCompany")?.Trim();
        var hireS    = StrArg(root, "hireDate")?.Trim();

        if (string.IsNullOrEmpty(realName)) return "错误：需要 realName";
        if (!deptId.HasValue || !supId.HasValue) return "错误：需要 deptId 与 supervisorId（用 user_search 查 userId）";
        if (string.IsNullOrEmpty(phone) || !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^1[3-9]\d{9}$"))
            return "错误：手机号格式不正确（11 位大陆手机号）";
        if (!string.IsNullOrEmpty(employeeNo) && !System.Text.RegularExpressions.Regex.IsMatch(employeeNo, @"^[A-Za-z0-9_-]{1,50}$"))
            return "错误：工号只能包含字母/数字/下划线/短横线";
        if (!string.IsNullOrEmpty(hireS) && !DateOnly.TryParse(hireS, out _))
            return "错误：hireDate 格式应为 yyyy-MM-dd";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var deptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct);
        if (deptName is null) return "错误：归属部门不存在";
        if (visibleIds is not null && !visibleIds.Contains(deptId.Value)) return "错误：归属部门不在你的管理范围内";
        if (supId.Value == operatorUserId) return "错误：直属上级不能是自己";
        var (vok, verr) = await ValidateSupervisorAsync(supId.Value, deptId.Value);
        if (!vok) return $"错误：{verr}";

        var param = JsonSerializer.Serialize(new
        {
            realName, deptId = deptId.Value, supervisorId = supId.Value, phone, employeeNo, position,
            contractCompany = contract, hireDate = string.IsNullOrEmpty(hireS) ? null : hireS
        });
        var summary = $"新建员工 {realName}：部门【{deptName}】" + (string.IsNullOrEmpty(employeeNo) ? "（工号自动生成）" : $"（工号 {employeeNo}）") + "，初始密码 123456";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "employee_create_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> EmployeeUpdateProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var userId = IntArg(root, "userId");
        if (!userId.HasValue || userId.Value <= 0) return "错误：需要 userId";

        var realName = StrArg(root, "realName")?.Trim();
        var deptId   = IntArg(root, "deptId");
        var supId    = IntArg(root, "supervisorId");
        var phone    = StrArg(root, "phone")?.Trim();
        var position = StrArg(root, "position")?.Trim();
        var contract = StrArg(root, "contractCompany")?.Trim();
        var hireS    = StrArg(root, "hireDate")?.Trim();
        if (realName is null && !deptId.HasValue && !supId.HasValue && phone is null && position is null && contract is null && hireS is null)
            return "错误：至少提供一个要修改的字段";
        if (phone is not null && !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^1[3-9]\d{9}$"))
            return "错误：手机号格式不正确";
        if (!string.IsNullOrEmpty(hireS) && !DateOnly.TryParse(hireS, out _))
            return "错误：hireDate 格式应为 yyyy-MM-dd";
        if (userId.Value == operatorUserId && deptId.HasValue)
            return "错误：不能修改自己的部门归属，请在员工管理页处理";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (target is null) return "错误：目标员工不存在";
        if (visibleIds is not null && (target.DepartmentId is null || !visibleIds.Contains(target.DepartmentId.Value)))
            return "错误：该员工不在你的管理范围内";

        var updateOp = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (updateOp is null || !Middlewares.CurrentUser.CanManageAccountCore(updateOp.Role, updateOp.ScopedDepartmentId, target.Role, target.ScopedDepartmentId))
            return "错误：无权操作该账号（角色层级限制）";

        var finalDeptId = deptId ?? target.DepartmentId;
        string? newDeptName = null;
        if (deptId.HasValue)
        {
            if (visibleIds is not null && !visibleIds.Contains(deptId.Value)) return "错误：新部门不在你的管理范围内";
            newDeptName = await db.Departments.Where(d => d.Id == deptId.Value).Select(d => d.DeptName).FirstOrDefaultAsync(ct);
            if (newDeptName is null) return "错误：新部门不存在";
        }
        if (supId.HasValue)
        {
            if (supId.Value == target.Id) return "错误：直属上级不能是自己";
            if (!finalDeptId.HasValue) return "错误：目标员工未分部门，不能设置直属上级";
            var (vok, verr) = await ValidateSupervisorAsync(supId.Value, finalDeptId.Value);
            if (!vok) return $"错误：{verr}";
        }

        var changes = new System.Collections.Generic.List<string>();
        if (realName is not null) changes.Add($"改名 {target.RealName}→{realName}");
        if (deptId.HasValue) changes.Add($"调部门→【{newDeptName}】");
        if (supId.HasValue) changes.Add("更换直属上级");
        if (phone is not null) changes.Add("改手机号");
        if (position is not null) changes.Add("改岗位");
        if (contract is not null) changes.Add("改合同公司");
        if (hireS is not null) changes.Add("改入职日期");

        var param = JsonSerializer.Serialize(new
        {
            userId = target.Id, realName, deptId, supervisorId = supId, phone, position,
            contractCompany = contract, hireDate = string.IsNullOrEmpty(hireS) ? null : hireS
        });
        var summary = $"修改员工 {target.RealName}（{target.EmployeeNo}）：{string.Join("、", changes)}";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "employee_update_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    private async Task<string> EmployeeBatchToggleProposeAsync(int operatorUserId, int conversationId, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root   = doc.RootElement;
        var action = StrArg(root, "action");
        var rawIds = new System.Collections.Generic.List<int>();
        if (root.TryGetProperty("userIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number) rawIds.Add(v.GetInt32());
        rawIds = rawIds.Distinct().ToList();
        if (action is not ("deactivate" or "activate")) return "错误：需要 action（deactivate/activate）";
        if (rawIds.Count == 0) return "错误：userIds 不能为空";

        var (visibleIds, err) = await LoadScopeAsync(operatorUserId, ct);
        if (err is not null) return err;
        if (rawIds.Contains(operatorUserId)) return "错误：列表中包含你自己，请移除后重试";

        var users = await db.Users.AsNoTracking()
            .Where(u => rawIds.Contains(u.Id))
            .Select(u => new { u.Id, u.RealName, u.EmployeeNo, u.DepartmentId, u.IsActive, u.IsBlacklisted, u.Role, u.ScopedDepartmentId })
            .ToListAsync(ct);
        if (users.Count != rawIds.Count) return "错误：部分目标员工不存在";
        var oob = users.Where(u => visibleIds is not null && (u.DepartmentId is null || !visibleIds.Contains(u.DepartmentId.Value)))
            .Select(u => u.EmployeeNo).ToList();
        if (oob.Count > 0) return $"错误：以下员工不在你的管理范围（{string.Join("、", oob.Take(5))}）";
        var bl = users.Where(u => u.IsBlacklisted).Select(u => u.EmployeeNo).ToList();
        if (bl.Count > 0) return $"错误：黑名单员工请单独处理（{string.Join("、", bl.Take(5))}）";

        // 角色层级：管不到的账号（比如文员提交里混了一个管理员）静默剔除、不中断整批——跟确认执行时
        // UserService.SetActiveBatchAsync 同一套"批量语义"（2026-09-29 第 12 轮审查发现）
        var batchOp = await db.Users.AsNoTracking()
            .Where(u => u.Id == operatorUserId)
            .Select(u => new { u.Role, u.ScopedDepartmentId })
            .FirstOrDefaultAsync(ct);
        if (batchOp is null) return "错误：操作者账号不存在";
        var unmanageable = users.Where(u => !Middlewares.CurrentUser.CanManageAccountCore(batchOp.Role, batchOp.ScopedDepartmentId, u.Role, u.ScopedDepartmentId))
            .Select(u => u.EmployeeNo).ToList();
        users = users.Where(u => Middlewares.CurrentUser.CanManageAccountCore(batchOp.Role, batchOp.ScopedDepartmentId, u.Role, u.ScopedDepartmentId)).ToList();

        var filtered = action == "deactivate"
            ? users.Where(u => u.IsActive).Select(u => u.Id).ToList()
            : users.Where(u => !u.IsActive).Select(u => u.Id).ToList();
        if (filtered.Count == 0) return "错误：这些员工已处于目标状态，或都无权操作，无需操作";

        var param = JsonSerializer.Serialize(new { userIds = filtered, action });
        var verb = action == "deactivate" ? "批量停用" : "批量启用";
        var skipHint = unmanageable.Count > 0 ? $"，已跳过 {unmanageable.Count} 个无权操作的账号（{string.Join("、", unmanageable.Take(5))}）" : "";
        var summary = $"{verb} {filtered.Count} 名员工（提交 {rawIds.Count} 人，已剔除无需操作的{skipHint}）";
        var (proposal, perr) = await CreateProposalAsync(operatorUserId, conversationId, "employee_batch_toggle_propose", param, summary, ct);
        return perr is not null ? perr
            : $"已生成待确认动作 #{proposal!.Id}：{summary}。该动作【不会自动执行】，请管理员点「确认执行」；15 分钟内有效。";
    }

    /// <summary>考勤组写权限（AGENT 采用"关联部门全部在范围内"口径，比页面 ANY 更严）：不受限=true；受限且零部门组=false。</summary>
    private async Task<bool> GroupWritableAsync(int groupId, HashSet<int>? visibleIds)
    {
        if (visibleIds is null) return true;
        var deptIds = await db.Departments.Where(d => d.AttendanceGroupId == groupId).Select(d => d.Id).ToListAsync();
        if (deptIds.Count == 0) return false;
        return deptIds.All(visibleIds.Contains);
    }

}
