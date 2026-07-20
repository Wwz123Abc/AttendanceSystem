using System.Text.Json.Serialization;

namespace AttendanceSystem.Models.DTOs;

// 本文件放的是和“钉钉对接”相关的 DTO。
// [JsonPropertyName("xxx")] 的作用：钉钉接口返回的字段名是 xxx（如 errcode），
// 用这个标记把它对应到我们这边的属性上，方便解析钉钉的 JSON 数据。

// ── 获取 accessToken（新版网关 /v1.0/oauth2/accessToken）响应 ─────────────────
public class DingTalkTokenResponse
{
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }   // 令牌

    /// <summary>有效期（秒），通常 7200（即 2 小时）</summary>
    [JsonPropertyName("expireIn")] public int ExpireIn { get; set; }
}

// ── attendance/list（拉打卡结果）请求体 ───────────────────────────────────────
public class DingTalkAttendanceListRequest
{
    /// <summary>查询起始工作日，格式 yyyy-MM-dd HH:mm:ss</summary>
    [JsonPropertyName("workDateFrom")] public string WorkDateFrom { get; set; } = string.Empty;

    /// <summary>查询结束工作日（与起始相差不超过 7 天）</summary>
    [JsonPropertyName("workDateTo")] public string WorkDateTo { get; set; } = string.Empty;

    /// <summary>要查的钉钉 userid 列表（一次最多 50 个）</summary>
    [JsonPropertyName("userIdList")] public List<string> UserIdList { get; set; } = [];

    [JsonPropertyName("offset")] public int Offset { get; set; }   // 翻页偏移量
    [JsonPropertyName("limit")]  public int Limit  { get; set; }   // 每页条数（最多 50）
    [JsonPropertyName("isI18n")] public bool IsI18n { get; set; }  // 是否国际化
}

// ── attendance/list 响应体 ────────────────────────────────────────────────────
public class DingTalkAttendanceListResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }      // 0 表示成功，非 0 是错误码
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }   // 错误说明

    /// <summary>打卡结果列表</summary>
    [JsonPropertyName("recordresult")] public List<DingTalkRecordResult>? RecordResult { get; set; }

    /// <summary>是否还有更多数据（true 就要继续翻页）</summary>
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }
}

/// <summary>钉钉单条打卡结果。</summary>
public class DingTalkRecordResult
{
    /// <summary>钉钉用户 userid</summary>
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;

    /// <summary>考勤日期（Unix 毫秒时间戳）</summary>
    [JsonPropertyName("workDate")] public long WorkDate { get; set; }

    /// <summary>打卡类型：OnDuty=上班，OffDuty=下班</summary>
    [JsonPropertyName("checkType")] public string CheckType { get; set; } = string.Empty;

    /// <summary>时间结果：Normal正常 / Late迟到 / SeriousLate严重迟到 / Early早退 / Absenteeism旷工 / NotSigned未打卡</summary>
    [JsonPropertyName("timeResult")] public string? TimeResult { get; set; }

    /// <summary>位置结果：Normal/Outside/NotSigned</summary>
    [JsonPropertyName("locationResult")] public string? LocationResult { get; set; }

    /// <summary>实际打卡时间（Unix 毫秒时间戳）</summary>
    [JsonPropertyName("userCheckTime")] public long UserCheckTime { get; set; }

    /// <summary>数据来源类型</summary>
    [JsonPropertyName("sourceType")] public string? SourceType { get; set; }

    [JsonPropertyName("userAddress")]   public string? UserAddress { get; set; }    // 打卡地址
    [JsonPropertyName("userLongitude")] public string? UserLongitude { get; set; }  // 经度
    [JsonPropertyName("userLatitude")]  public string? UserLatitude { get; set; }   // 纬度
}

// ── attendance/getleavestatus（拉请假记录）请求体 ─────────────────────────────
// 打卡结果接口（attendance/list）不返回请假，请假是钉钉单独的「请假状态」接口。
public class DingTalkLeaveStatusRequest
{
    /// <summary>要查的钉钉 userid 列表，英文逗号分隔（一次最多 100 个）</summary>
    [JsonPropertyName("userid_list")] public string UserIdList { get; set; } = string.Empty;

    /// <summary>查询起始时间（Unix 毫秒时间戳）</summary>
    [JsonPropertyName("start_time")] public long StartTime { get; set; }

    /// <summary>查询结束时间（Unix 毫秒时间戳）</summary>
    [JsonPropertyName("end_time")] public long EndTime { get; set; }

    [JsonPropertyName("offset")] public long Offset { get; set; }   // 翻页偏移量
    [JsonPropertyName("size")]   public long Size   { get; set; }   // 每页条数（最多 20）
}

// ── attendance/getleavestatus 响应体 ─────────────────────────────────────────
public class DingTalkLeaveStatusResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }      // 0 表示成功
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }   // 错误说明
    [JsonPropertyName("result")]  public DingTalkLeaveStatusResult? Result { get; set; }
}

public class DingTalkLeaveStatusResult
{
    /// <summary>是否还有更多数据（true 就要继续翻页）</summary>
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }

    /// <summary>请假记录列表</summary>
    [JsonPropertyName("leave_status")] public List<DingTalkLeaveStatus>? LeaveStatus { get; set; }
}

/// <summary>钉钉单条请假记录。</summary>
public class DingTalkLeaveStatus
{
    /// <summary>钉钉用户 userid</summary>
    [JsonPropertyName("userid")] public string UserId { get; set; } = string.Empty;

    /// <summary>假期类型 code（如年假/事假/病假的编码）</summary>
    [JsonPropertyName("leave_code")] public string? LeaveCode { get; set; }

    /// <summary>假期类型名称（部分企业返回，没有则用 code/“请假”兜底）</summary>
    [JsonPropertyName("leave_name")] public string? LeaveName { get; set; }

    /// <summary>请假开始时间（Unix 毫秒时间戳）</summary>
    [JsonPropertyName("start_time")] public long StartTime { get; set; }

    /// <summary>请假结束时间（Unix 毫秒时间戳）</summary>
    [JsonPropertyName("end_time")] public long EndTime { get; set; }

    // 说明：钉钉还会返回 duration_unit / duration_percent 等字段，但各企业返回的类型不一致
    //（有的是字符串枚举、有的是数字），本系统用不到，故不在此映射，由 System.Text.Json 自动忽略，避免解析报错。
}

// ── 工作通知（topapi/message/corpconversation/asyncsend_v2），找回密码验证码走这个接口发送 ──
public class DingTalkSendMsgRequest
{
    /// <summary>应用 AgentId（钉钉后台"应用信息"页查看，不是 AppKey）</summary>
    [JsonPropertyName("agent_id")] public long AgentId { get; set; }

    /// <summary>接收人的钉钉 userid，多个用逗号分隔</summary>
    [JsonPropertyName("userid_list")] public string UserIdList { get; set; } = string.Empty;

    [JsonPropertyName("msg")] public DingTalkMsgBody Msg { get; set; } = new();
}

/// <summary>工作通知消息体（这里只用纯文本类型）。</summary>
public class DingTalkMsgBody
{
    [JsonPropertyName("msgtype")] public string MsgType { get; set; } = "text";
    [JsonPropertyName("text")]    public DingTalkMsgText Text { get; set; } = new();
}

public class DingTalkMsgText
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

public class DingTalkSendMsgResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")]    public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]     public string? ErrMsg { get; set; }
    [JsonPropertyName("task_id")]    public long? TaskId { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
}

// ── 同步触发请求 / 结果 ───────────────────────────────────────────────────────
public class DingTalkSyncRequestDto
{
    /// <summary>同步起始时间（不传默认昨天 00:00）</summary>
    public DateTime? From { get; set; }

    /// <summary>同步结束时间（不传默认今天 23:59）</summary>
    public DateTime? To { get; set; }
}

/// <summary>“立即同步打卡”完成后返回的结果。</summary>
public class DingTalkSyncResultDto
{
    public bool   Success        { get; set; } = true;
    public string Message        { get; set; } = string.Empty;

    /// <summary>参与同步的已映射用户数</summary>
    public int    MappedUsers    { get; set; }

    /// <summary>从钉钉拉取的打卡结果条数</summary>
    public int    Pulled         { get; set; }

    /// <summary>新增的原始打卡流水条数</summary>
    public int    PunchAdded     { get; set; }

    /// <summary>新增/更新的考勤日记录条数</summary>
    public int    RecordUpserted { get; set; }

    /// <summary>从钉钉拉取的请假记录条数</summary>
    public int    LeavePulled    { get; set; }

    /// <summary>因请假而置为「请假」的考勤日数</summary>
    public int    LeaveApplied   { get; set; }

    /// <summary>请假同步被跳过的原因（如未开通考勤/假期权限）；为空表示正常</summary>
    public string? LeaveSkippedReason { get; set; }

    public DateTime From { get; set; }
    public DateTime To   { get; set; }
}

/// <summary>“自动映射”（按工号回填 DingTalkUserId）完成后返回的结果。</summary>
public class DingTalkAutoMapResultDto
{
    public bool   Success  { get; set; } = true;
    public string Message  { get; set; } = string.Empty;

    /// <summary>待映射用户数（有工号、且尚未映射；勾选覆盖时为全部有工号者）</summary>
    public int    Total    { get; set; }

    /// <summary>成功映射数</summary>
    public int    Mapped   { get; set; }

    /// <summary>工号在钉钉通讯录里查不到的数量</summary>
    public int    NotFound { get; set; }
}

// ── 通讯录导入（从钉钉把员工整批导入本地）─────────────────────────────────────

/// <summary>钉钉服务端接口的通用响应（都带 errcode/errmsg），便于统一判断成功失败。</summary>
public interface IDingTalkResponse
{
    int     ErrCode { get; }
    string? ErrMsg  { get; }
}

// 删除企业员工（topapi/v2/user/delete）的响应：只有 errcode/errmsg，没有额外数据
public class DingTalkDeleteUserResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }
}

// 更新企业员工（topapi/v2/user/update）的请求体：userid 必填，其余字段不传就表示"这一项不改"
public class DingTalkUpdateUserRequest
{
    [JsonPropertyName("userid")]       public string  UserId     { get; set; } = string.Empty;
    [JsonPropertyName("name")]         public string?  Name       { get; set; }
    [JsonPropertyName("mobile")]       public string?  Mobile     { get; set; }
    [JsonPropertyName("job_number")]   public string?  JobNumber  { get; set; }
    [JsonPropertyName("title")]        public string?  Title      { get; set; }

    /// <summary>要调到的钉钉部门编号列表，多个用逗号分隔；不传表示部门不变（和 CreateUserRequest 同一种格式）</summary>
    [JsonPropertyName("dept_id_list")] public string?  DeptIdList { get; set; }
}

// 更新企业员工的响应：只有 errcode/errmsg，没有额外数据
public class DingTalkUpdateUserResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }
}

// 新建企业员工（topapi/v2/user/create）的请求体：name/mobile/dept_id_list 是必填项
public class DingTalkCreateUserRequest
{
    [JsonPropertyName("name")]         public string  Name       { get; set; } = string.Empty;
    [JsonPropertyName("mobile")]       public string  Mobile     { get; set; } = string.Empty;   // 企业内必须唯一
    [JsonPropertyName("job_number")]   public string? JobNumber  { get; set; }
    [JsonPropertyName("title")]        public string? Title      { get; set; }

    /// <summary>要挂到的钉钉部门编号列表，多个用逗号分隔（钉钉这个接口要求的是逗号分隔字符串，不是数组）</summary>
    [JsonPropertyName("dept_id_list")] public string  DeptIdList { get; set; } = string.Empty;
}

// 新建企业员工的响应：成功会在 result.userid 里带回钉钉分配的员工编号
public class DingTalkCreateUserResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }
    [JsonPropertyName("result")]  public DingTalkCreateUserResult? Result { get; set; }
}

public class DingTalkCreateUserResult
{
    [JsonPropertyName("userid")] public string? UserId { get; set; }
}

// 获取子部门列表（topapi/v2/department/listsub）的响应
public class DingTalkDeptListResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }
    [JsonPropertyName("result")]  public List<DingTalkDept>? Result { get; set; }
}

/// <summary>钉钉的一个部门。</summary>
public class DingTalkDept
{
    [JsonPropertyName("dept_id")]   public long DeptId { get; set; }      // 部门编号
    [JsonPropertyName("name")]      public string? Name { get; set; }     // 部门名称
    [JsonPropertyName("parent_id")] public long ParentId { get; set; }    // 上级部门编号
}

// 获取部门成员详情（topapi/v2/user/list）的响应
public class DingTalkUserListResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }
    [JsonPropertyName("result")]  public DingTalkUserListResult? Result { get; set; }
}

public class DingTalkUserListResult
{
    [JsonPropertyName("has_more")]    public bool HasMore { get; set; }       // 是否还有下一页
    [JsonPropertyName("next_cursor")] public int NextCursor { get; set; }     // 下一页的游标
    [JsonPropertyName("list")]        public List<DingTalkDeptUser>? List { get; set; }   // 本页员工
}

/// <summary>钉钉通讯录里的一个员工。</summary>
public class DingTalkDeptUser
{
    [JsonPropertyName("userid")]       public string UserId { get; set; } = string.Empty;  // 钉钉 userid
    [JsonPropertyName("name")]         public string? Name { get; set; }                   // 姓名
    [JsonPropertyName("mobile")]       public string? Mobile { get; set; }                 // 手机号
    [JsonPropertyName("job_number")]   public string? JobNumber { get; set; }              // 工号
    [JsonPropertyName("title")]        public string? Title { get; set; }                  // 职位

    /// <summary>入职时间（Unix 毫秒时间戳，钉钉未填则为 null）</summary>
    [JsonPropertyName("hired_date")]   public long? HiredDate { get; set; }

    /// <summary>该员工所属的部门编号列表（取第一个作为主部门）</summary>
    [JsonPropertyName("dept_id_list")] public List<long>? DeptIdList { get; set; }
}

// 获取单个部门详情（topapi/v2/department/get）的响应；用来取根部门名作为“公司名”
public class DingTalkDeptGetResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }
    [JsonPropertyName("result")]  public DingTalkDept? Result { get; set; }
}

/// <summary>一次性拉取的通讯录快照：公司名 + 全部部门 + 全部员工。</summary>
public class DingTalkContactSnapshot
{
    public string?               CompanyName { get; set; }       // 公司名（取自根部门）
    public List<DingTalkDept>    Departments { get; set; } = []; // 所有部门
    public List<DingTalkDeptUser> Users      { get; set; } = []; // 所有员工
}

// ── 部门同步（本系统 → 钉钉，新建/改名/删除部门时联动）──────────────────────────

// 新建部门（topapi/v2/department/create）的请求体：name/parent_id 必填
public class DingTalkCreateDeptRequest
{
    [JsonPropertyName("name")]      public string Name     { get; set; } = string.Empty;
    [JsonPropertyName("parent_id")] public long   ParentId { get; set; }   // 顶级部门的上级填 1（钉钉根部门）
}

// 新建部门的响应：成功会在 result.dept_id 里带回钉钉分配的部门编号
public class DingTalkCreateDeptResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }
    [JsonPropertyName("result")]  public DingTalkCreateDeptResult? Result { get; set; }
}

public class DingTalkCreateDeptResult
{
    [JsonPropertyName("dept_id")] public long DeptId { get; set; }
}

// 更新部门（topapi/v2/department/update）的请求体：dept_id 必填，其余不传表示这一项不改
public class DingTalkUpdateDeptRequest
{
    [JsonPropertyName("dept_id")]   public long    DeptId   { get; set; }
    [JsonPropertyName("name")]      public string? Name     { get; set; }
    [JsonPropertyName("parent_id")] public long?   ParentId { get; set; }
}

// 更新部门的响应：只有 errcode/errmsg，没有额外数据
public class DingTalkUpdateDeptResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }
}

// 删除部门（topapi/v2/department/delete）的响应：只有 errcode/errmsg，没有额外数据
public class DingTalkDeleteDeptResponse : IDingTalkResponse
{
    [JsonPropertyName("errcode")] public int ErrCode { get; set; }
    [JsonPropertyName("errmsg")]  public string? ErrMsg { get; set; }
}

/// <summary>”从钉钉导入员工”完成后返回的结果。</summary>
public class DingTalkImportResultDto
{
    public bool   Success           { get; set; } = true;
    public string Message           { get; set; } = string.Empty;

    /// <summary>从钉钉通讯录读到的员工总数</summary>
    public int    TotalFromDingTalk { get; set; }

    /// <summary>新建的本地员工数</summary>
    public int    Created           { get; set; }

    /// <summary>更新/对接上的本地员工数</summary>
    public int    Updated           { get; set; }

    /// <summary>同步的部门数</summary>
    public int    DepartmentsSynced { get; set; }
}
