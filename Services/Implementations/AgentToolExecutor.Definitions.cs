using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>工具定义（参数用 JSON Schema 描述，模型据此填参）（<see cref="AgentToolExecutor"/> 的一部分）。</summary>
public partial class AgentToolExecutor
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
            "【高风险·写操作·需管理员确认】彻底删除某员工账号（不可恢复）。只允许删除没有任何考勤/打卡/申请记录的账号（比如误建的空账号）；有历史数据的员工请用 user_toggle_propose 停用。只生成待确认动作；请先与管理员确认。",
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
}
